// AFU Zip Explorer eklentisi: IExplorerCommand.
//
// İki yerden yüklenir:
//  - Windows 11'in yeni sağ tık menüsü: imzalı sparse paket (AfuZip.ContextMenu.msix) bu DLL'i paketli COM sunucusu
//    olarak bildirir (CLSID_PackagedMenu).
//  - Klasik menü ("Daha fazla seçenek göster", Windows 10): HKCU'daki fiil ExplorerCommandHandler ile bu DLL'e
//    yönlenir (CLSID_ClassicMenu); 7-Zip gibi "proje\ klasörüne çıkar" başlıkları buradan gelir.
//
// Komutlar yalnızca yanındaki AfuZip.exe'yi bayraklarla başlatır; arşiv işini uygulama yapar. Menü kurulurken diske
// dokunulmaz (Explorer'ı bekletmemek için), yalnızca uzantı ve öğe öznitelikleri okunur.

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>

#include <new>
#include <string>
#include <vector>

#include "Naming.h"

namespace {

using namespace afuzip;

// {B8EC4BAE-41F4-4C44-86CE-8B717D7D4E6D}: sparse paketin bildirdiği sınıf (yeni menü).
constexpr CLSID CLSID_PackagedMenu = {0xb8ec4bae, 0x41f4, 0x4c44, {0x86, 0xce, 0x8b, 0x71, 0x7d, 0x7d, 0x4e, 0x6d}};
// {4538D22E-A004-4452-8898-AF47558519CB}: HKCU'ya kaydedilen sınıf (klasik menü).
constexpr CLSID CLSID_ClassicMenu = {0x4538d22e, 0xa004, 0x4452, {0x88, 0x98, 0xaf, 0x47, 0x55, 0x85, 0x19, 0xcb}};

HMODULE g_module = nullptr;
long g_objects = 0;
long g_locks = 0;

// Komut satırı sınırı 32.767 karakter; çok sayıda seçili dosya bir listeye yazılıp --paths-from ile verilir.
constexpr size_t kMaxCommandLine = 30000;

std::wstring ModuleDirectory()
{
    std::wstring path(MAX_PATH, L'\0');
    for (;;)
    {
        auto length = GetModuleFileNameW(g_module, path.data(), static_cast<DWORD>(path.size()));
        if (length == 0)
            return L"";
        if (length < path.size())
        {
            path.resize(length);
            break;
        }
        path.resize(path.size() * 2);
    }
    auto slash = path.find_last_of(L'\\');
    return slash == std::wstring::npos ? path : path.substr(0, slash);
}

std::wstring ExePath()
{
    return ModuleDirectory() + L"\\AfuZip.exe";
}

/// Seçimin dosya sistemi yolları. Sanal öğe (Ağ, Bu bilgisayar, arşivin içindeki öğe) varsa boş döner.
std::vector<Item> ReadItems(IShellItemArray* array)
{
    std::vector<Item> items;
    DWORD count = 0;
    if (array == nullptr || FAILED(array->GetCount(&count)) || count == 0)
        return items;

    items.reserve(count);
    for (DWORD i = 0; i < count; i++)
    {
        IShellItem* item = nullptr;
        if (FAILED(array->GetItemAt(i, &item)))
            return {};

        PWSTR path = nullptr;
        SFGAOF attributes = 0;
        auto ok = SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path != nullptr;
        item->GetAttributes(SFGAO_FOLDER | SFGAO_STREAM, &attributes);
        item->Release();
        if (!ok)
            return {};

        // Zip dosyası Explorer'da hem klasör hem akış görünür; akışsa dosya sayılır.
        items.push_back({path, (attributes & SFGAO_FOLDER) != 0 && (attributes & SFGAO_STREAM) == 0});
        CoTaskMemFree(path);
    }
    return items;
}

bool AllArchives(const std::vector<Item>& items)
{
    if (items.empty())
        return false;
    for (const auto& item : items)
    {
        if (item.folder || !IsArchiveName(item.path))
            return false;
    }
    return true;
}

std::wstring Quote(std::wstring path)
{
    // "C:\" gibi ters bölüyle biten yol, kapanış tırnağını kaçırmasın.
    if (!path.empty() && path.back() == L'\\')
        path.push_back(L'.');
    return L"\"" + path + L"\"";
}

bool WriteUtf8(const std::wstring& file, const std::wstring& text)
{
    auto size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    std::string bytes(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), bytes.data(), size, nullptr, nullptr);

    auto handle = CreateFileW(file.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY, nullptr);
    if (handle == INVALID_HANDLE_VALUE)
        return false;
    DWORD written = 0;
    auto ok = WriteFile(handle, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) && written == bytes.size();
    CloseHandle(handle);
    return ok;
}

HRESULT Launch(const std::wstring& flags, const std::vector<Item>& items)
{
    auto exe = ExePath();
    std::wstring arguments = flags;
    for (const auto& item : items)
        arguments += L" " + Quote(item.path);

    if (arguments.size() + exe.size() > kMaxCommandLine)
    {
        // Çok sayıda seçim: yollar %TEMP%\AfuZip altında bir listeye yazılır, uygulama okuyup siler.
        std::wstring temp(MAX_PATH + 1, L'\0');
        temp.resize(GetTempPathW(static_cast<DWORD>(temp.size()), temp.data()));
        temp += L"AfuZip";
        CreateDirectoryW(temp.c_str(), nullptr);
        auto list = temp + L"\\secim-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()) + L".txt";

        std::wstring text;
        for (const auto& item : items)
            text += item.path + L"\r\n";
        if (!WriteUtf8(list, text))
            return HRESULT_FROM_WIN32(GetLastError());
        arguments = flags + L" --paths-from " + Quote(list);
    }

    auto commandLine = Quote(exe) + L" " + arguments;
    std::vector<wchar_t> buffer(commandLine.begin(), commandLine.end());
    buffer.push_back(L'\0');

    // Explorer ön plandadır; yeni sürecin penceresi (ilerleme, diyalog) öne gelebilsin.
    AllowSetForegroundWindow(ASFW_ANY);
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(exe.c_str(), buffer.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process))
        return HRESULT_FROM_WIN32(GetLastError());
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return S_OK;
}

HRESULT DupString(const std::wstring& text, LPWSTR* result)
{
    if (result == nullptr)
        return E_POINTER;
    return SHStrDupW(text.c_str(), result);
}

// --- Komutlar ----------------------------------------------------------------------------------

enum class Kind
{
    Open,
    ExtractHere,
    ExtractFolder,
    Test,
    Add,
    Compress7z,
    CompressZip,
};

class SubCommand final : public IExplorerCommand
{
public:
    explicit SubCommand(Kind kind) : kind_(kind) { InterlockedIncrement(&g_objects); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** result) override
    {
        if (result == nullptr)
            return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, __uuidof(IExplorerCommand)))
        {
            *result = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        *result = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&refs_)); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        auto count = InterlockedDecrement(&refs_);
        if (count == 0)
            delete this;
        return static_cast<ULONG>(count);
    }

    IFACEMETHODIMP GetTitle(IShellItemArray* array, LPWSTR* name) override
    {
        auto items = ReadItems(array);
        switch (kind_)
        {
        case Kind::Open:
            return DupString(L"AFU Zip'te aç", name);
        case Kind::ExtractHere:
            return DupString(L"Buraya çıkar", name);
        case Kind::ExtractFolder:
            return DupString(items.size() == 1 ? L"\"" + ArchiveStem(items[0].path) + L"\\\" klasörüne çıkar"
                                               : std::wstring(L"Her birini kendi klasörüne çıkar"),
                             name);
        case Kind::Test:
            return DupString(L"Test et", name);
        case Kind::Add:
            return DupString(L"Arşive ekle…", name);
        case Kind::Compress7z:
            return DupString(L"\"" + CompressName(items, L".7z") + L"\" olarak sıkıştır", name);
        case Kind::CompressZip:
            return DupString(L"\"" + CompressName(items, L".zip") + L"\" olarak sıkıştır", name);
        }
        return E_UNEXPECTED;
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override { return DupString(ExePath() + L",0", icon); }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override
    {
        if (tip != nullptr)
            *tip = nullptr;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetCanonicalName(GUID* guid) override
    {
        if (guid != nullptr)
            *guid = GUID_NULL;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetState(IShellItemArray* array, BOOL, EXPCMDSTATE* state) override
    {
        if (state == nullptr)
            return E_POINTER;
        auto items = ReadItems(array);
        auto visible = !items.empty();
        if (visible && (kind_ == Kind::Open || kind_ == Kind::ExtractHere || kind_ == Kind::ExtractFolder || kind_ == Kind::Test))
            visible = AllArchives(items);
        *state = visible ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* array, IBindCtx*) override
    {
        auto items = ReadItems(array);
        if (items.empty())
            return E_INVALIDARG;
        switch (kind_)
        {
        case Kind::Open:
            return Launch(L"", items);
        case Kind::ExtractHere:
            return Launch(L"--extract-here", items);
        case Kind::ExtractFolder:
            return Launch(L"--extract-folder", items);
        case Kind::Test:
            return Launch(L"--test", items);
        case Kind::Add:
            return Launch(L"--add", items);
        case Kind::Compress7z:
            return Launch(L"--compress balanced", items);
        case Kind::CompressZip:
            return Launch(L"--compress share", items);
        }
        return E_UNEXPECTED;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        if (flags == nullptr)
            return E_POINTER;
        *flags = kind_ == Kind::Test || kind_ == Kind::Add ? ECF_SEPARATORBEFORE : ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** result) override
    {
        if (result != nullptr)
            *result = nullptr;
        return E_NOTIMPL;
    }

private:
    ~SubCommand() { InterlockedDecrement(&g_objects); }

    long refs_ = 1;
    Kind kind_;
};

class CommandEnumerator final : public IEnumExplorerCommand
{
public:
    CommandEnumerator()
    {
        InterlockedIncrement(&g_objects);
        for (auto kind : {Kind::Open, Kind::ExtractHere, Kind::ExtractFolder, Kind::Test, Kind::Add, Kind::Compress7z, Kind::CompressZip})
        {
            auto command = new (std::nothrow) SubCommand(kind);
            if (command != nullptr)
                commands_.push_back(command);
        }
    }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** result) override
    {
        if (result == nullptr)
            return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, __uuidof(IEnumExplorerCommand)))
        {
            *result = static_cast<IEnumExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        *result = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&refs_)); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        auto count = InterlockedDecrement(&refs_);
        if (count == 0)
            delete this;
        return static_cast<ULONG>(count);
    }

    IFACEMETHODIMP Next(ULONG count, IExplorerCommand** commands, ULONG* fetched) override
    {
        if (commands == nullptr)
            return E_POINTER;
        ULONG done = 0;
        while (done < count && position_ < commands_.size())
        {
            commands[done] = commands_[position_++];
            commands[done]->AddRef();
            done++;
        }
        if (fetched != nullptr)
            *fetched = done;
        return done == count ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP Skip(ULONG count) override
    {
        position_ = position_ + count > commands_.size() ? commands_.size() : position_ + count;
        return position_ < commands_.size() ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP Reset() override
    {
        position_ = 0;
        return S_OK;
    }
    IFACEMETHODIMP Clone(IEnumExplorerCommand** result) override
    {
        if (result != nullptr)
            *result = nullptr;
        return E_NOTIMPL;
    }

private:
    ~CommandEnumerator()
    {
        for (auto command : commands_)
            command->Release();
        InterlockedDecrement(&g_objects);
    }

    long refs_ = 1;
    size_t position_ = 0;
    std::vector<IExplorerCommand*> commands_;
};

/// Menüdeki "AFU Zip" öğesi; alt komutlar seçime göre kendilerini gizler.
class RootCommand final : public IExplorerCommand
{
public:
    RootCommand() { InterlockedIncrement(&g_objects); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** result) override
    {
        if (result == nullptr)
            return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, __uuidof(IExplorerCommand)))
        {
            *result = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        *result = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&refs_)); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        auto count = InterlockedDecrement(&refs_);
        if (count == 0)
            delete this;
        return static_cast<ULONG>(count);
    }

    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override { return DupString(L"AFU Zip", name); }
    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override { return DupString(ExePath() + L",0", icon); }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override
    {
        if (tip != nullptr)
            *tip = nullptr;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetCanonicalName(GUID* guid) override
    {
        if (guid == nullptr)
            return E_POINTER;
        *guid = CLSID_PackagedMenu;
        return S_OK;
    }
    IFACEMETHODIMP GetState(IShellItemArray* array, BOOL, EXPCMDSTATE* state) override
    {
        if (state == nullptr)
            return E_POINTER;
        *state = ReadItems(array).empty() ? ECS_HIDDEN : ECS_ENABLED;
        return S_OK;
    }
    IFACEMETHODIMP Invoke(IShellItemArray*, IBindCtx*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        if (flags == nullptr)
            return E_POINTER;
        *flags = ECF_HASSUBCOMMANDS;
        return S_OK;
    }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** result) override
    {
        if (result == nullptr)
            return E_POINTER;
        *result = new (std::nothrow) CommandEnumerator();
        return *result != nullptr ? S_OK : E_OUTOFMEMORY;
    }

private:
    ~RootCommand() { InterlockedDecrement(&g_objects); }

    long refs_ = 1;
};

class ClassFactory final : public IClassFactory
{
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** result) override
    {
        if (result == nullptr)
            return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory))
        {
            *result = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *result = nullptr;
        return E_NOINTERFACE;
    }
    // Fabrika statik: sayaç modülün yüklü kalmasını belirlemez.
    IFACEMETHODIMP_(ULONG) AddRef() override { return 2; }
    IFACEMETHODIMP_(ULONG) Release() override { return 1; }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** result) override
    {
        if (result == nullptr)
            return E_POINTER;
        *result = nullptr;
        if (outer != nullptr)
            return CLASS_E_NOAGGREGATION;
        auto command = new (std::nothrow) RootCommand();
        if (command == nullptr)
            return E_OUTOFMEMORY;
        auto hr = command->QueryInterface(riid, result);
        command->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock)
            InterlockedIncrement(&g_locks);
        else
            InterlockedDecrement(&g_locks);
        return S_OK;
    }
};

ClassFactory g_factory;

} // namespace

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void** result)
{
    if (result == nullptr)
        return E_POINTER;
    *result = nullptr;
    if (!IsEqualCLSID(clsid, CLSID_PackagedMenu) && !IsEqualCLSID(clsid, CLSID_ClassicMenu))
        return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, result);
}

extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return g_objects == 0 && g_locks == 0 ? S_OK : S_FALSE;
}

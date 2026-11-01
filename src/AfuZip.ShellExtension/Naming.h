// Menü başlıkları için ad hesapları (SmartExtract.ArchiveStem ve ArchiveNaming.DefaultTarget ile aynı kurallar).
// Windows'a bağımlı değil: Linux'ta da derlenip test edilir (tests/naming_test.cpp).
#pragma once

#include <cwchar>
#include <cwctype>
#include <string>
#include <vector>

namespace afuzip {

// ShellRegistration.Extensions ile aynı liste (zip tabanlı belgeler bilerek yok).
const wchar_t* const kArchiveExtensions[] = {
    L".7z", L".zip", L".zipx", L".rar", L".cbz", L".cbr", L".tar", L".gz", L".tgz", L".bz2", L".tbz2", L".xz", L".txz",
    L".zst", L".tzst", L".lz4", L".lzma", L".br", L".iso", L".cab", L".wim", L".esd", L".001",
};

// SmartExtract.ArchiveStem ile aynı bileşik uzantılar.
const wchar_t* const kCompoundExtensions[] = {
    L".tar.gz", L".tar.bz2", L".tar.xz", L".tar.zst", L".tar.lz4", L".tar.br", L".tar.lzma", L".tar.z",
};

inline std::wstring Lower(std::wstring text)
{
    for (auto& c : text)
        c = static_cast<wchar_t>(std::towlower(c));
    return text;
}

inline bool EndsWith(const std::wstring& text, const std::wstring& suffix)
{
    return text.size() >= suffix.size() && text.compare(text.size() - suffix.size(), suffix.size(), suffix) == 0;
}

inline std::wstring TrimSlash(std::wstring path)
{
    while (path.size() > 3 && (path.back() == L'\\' || path.back() == L'/'))
        path.pop_back();
    return path;
}

inline std::wstring FileName(const std::wstring& path)
{
    auto trimmed = TrimSlash(path);
    auto slash = trimmed.find_last_of(L"\\/");
    return slash == std::wstring::npos ? trimmed : trimmed.substr(slash + 1);
}

inline std::wstring ParentName(const std::wstring& path)
{
    auto trimmed = TrimSlash(path);
    auto slash = trimmed.find_last_of(L"\\/");
    if (slash == std::wstring::npos)
        return L"";
    auto parent = trimmed.substr(0, slash);
    // "C:\x" → parent "C:" (kök): adı yok.
    if (parent.size() <= 2)
        return L"";
    return FileName(parent);
}

inline std::wstring WithoutExtension(const std::wstring& name)
{
    auto dot = name.find_last_of(L'.');
    // .NET Path.GetFileNameWithoutExtension gibi: ".zip" → "" (çağıran "arşiv"e düşer).
    return dot == std::wstring::npos ? name : name.substr(0, dot);
}

inline bool IsArchiveName(const std::wstring& path)
{
    auto lower = Lower(path);
    for (auto extension : kArchiveExtensions)
    {
        if (EndsWith(lower, extension))
            return true;
    }
    return false;
}

/// "yedek.tar.gz" → "yedek", "film.part1.rar" → "film", "proje.7z.001" → "proje".
inline std::wstring ArchiveStem(const std::wstring& path)
{
    auto name = FileName(path);
    auto lower = Lower(name);

    // Cilt eki: ".001" ya da ".part1.rar" / ".part01.rar".
    if (lower.size() > 4 && lower[lower.size() - 4] == L'.' && std::iswdigit(lower[lower.size() - 3]) &&
        std::iswdigit(lower[lower.size() - 2]) && std::iswdigit(lower[lower.size() - 1]))
    {
        name.resize(name.size() - 4);
        lower.resize(lower.size() - 4);
    }
    else if (EndsWith(lower, L".rar"))
    {
        // ".part1.rar", ".part01.rar": ek bütünüyle düşer (C#'taki düzenli ifadeyle aynı).
        auto part = lower.rfind(L".part");
        if (part != std::wstring::npos && part + 5 < lower.size() - 4)
        {
            auto digits = lower.substr(part + 5, lower.size() - 4 - (part + 5));
            bool allDigits = true;
            for (auto c : digits)
                allDigits = allDigits && std::iswdigit(c);
            if (allDigits && digits.back() == L'1' && digits.find_first_not_of(L'0') == digits.size() - 1)
            {
                name.resize(part);
                lower.resize(part);
            }
        }
    }

    for (auto compound : kCompoundExtensions)
    {
        if (EndsWith(lower, compound))
        {
            auto stem = name.substr(0, name.size() - wcslen(compound));
            return stem.empty() ? L"arşiv" : stem;
        }
    }
    auto stem = WithoutExtension(name);
    return stem.empty() ? L"arşiv" : stem;
}

struct Item
{
    std::wstring path;
    bool folder = false;
};

/// ArchiveNaming.DefaultTarget ile aynı ad: tek öğe → onun adı, birden fazla → bulundukları klasörün adı.
inline std::wstring CompressName(const std::vector<Item>& items, const wchar_t* extension)
{
    std::wstring name;
    if (items.size() == 1)
        name = items[0].folder ? FileName(items[0].path) : WithoutExtension(FileName(items[0].path));
    else if (!items.empty())
        name = ParentName(items[0].path);
    if (name.empty() || name.find(L':') != std::wstring::npos)
        name = L"Arşiv";
    return name + extension;
}

} // namespace afuzip

// Menü başlıklarındaki ad kuralları: C# tarafındaki SmartExtract/ArchiveNaming testleriyle aynı örnekler.
// Linux'ta: g++ -std=c++17 -I.. naming_test.cpp && ./a.out
#include <clocale>
#include <cstdio>
#include "../Naming.h"

using namespace afuzip;

static int failures = 0;

static void Expect(const std::wstring& actual, const std::wstring& expected, const char* what)
{
    if (actual != expected)
    {
        failures++;
        std::printf("HATA %s: '%ls' bekleniyordu, '%ls' geldi\n", what, expected.c_str(), actual.c_str());
    }
}

static void ExpectBool(bool actual, bool expected, const char* what)
{
    if (actual != expected)
    {
        failures++;
        std::printf("HATA %s\n", what);
    }
}

int main()
{
    std::setlocale(LC_ALL, "C.UTF-8");
    Expect(ArchiveStem(L"C:\\İndirilenler\\proje.7z"), L"proje", "7z");
    Expect(ArchiveStem(L"C:\\x\\yedek.tar.gz"), L"yedek", "tar.gz");
    Expect(ArchiveStem(L"C:\\x\\yedek.TAR.ZST"), L"yedek", "TAR.ZST");
    Expect(ArchiveStem(L"C:\\x\\film.part1.rar"), L"film", "part1.rar");
    Expect(ArchiveStem(L"C:\\x\\film.part01.rar"), L"film", "part01.rar");
    Expect(ArchiveStem(L"C:\\x\\film.part2.rar"), L"film.part2", "part2.rar");
    Expect(ArchiveStem(L"C:\\x\\proje.7z.001"), L"proje", "7z.001");
    Expect(ArchiveStem(L"C:\\x\\notlar.zip"), L"notlar", "zip");
    Expect(ArchiveStem(L"C:\\x\\.zip"), L"arşiv", "isimsiz");

    ExpectBool(IsArchiveName(L"C:\\a\\B.ZIP"), true, "büyük harf zip");
    ExpectBool(IsArchiveName(L"C:\\a\\rapor.docx"), false, "docx arşiv değil");
    ExpectBool(IsArchiveName(L"C:\\a\\x.7z.001"), true, "001");

    Expect(CompressName({{L"C:\\Kullanıcılar\\ali\\Belgeler", true}}, L".7z"), L"Belgeler.7z", "tek klasör");
    Expect(CompressName({{L"C:\\x\\rapor.docx", false}}, L".zip"), L"rapor.zip", "tek dosya");
    Expect(CompressName({{L"C:\\x\\Proje\\a.txt", false}, {L"C:\\x\\Proje\\b.txt", false}}, L".7z"), L"Proje.7z", "çoklu");
    Expect(CompressName({{L"C:\\a.txt", false}, {L"C:\\b.txt", false}}, L".7z"), L"Arşiv.7z", "kökte çoklu");
    Expect(CompressName({{L"D:\\", true}}, L".7z"), L"Arşiv.7z", "sürücü kökü");

    std::printf(failures == 0 ? "Tüm ad testleri geçti\n" : "%d hata\n", failures);
    return failures == 0 ? 0 : 1;
}

using System.Diagnostics;
using AfuZip.Engine.Managed;
using AfuZip.Engine.SevenZip;

namespace AfuZip.Tests;

/// <summary>
/// Faz 0 spike'ının çıkış kriteri: C#'tan 7z.dll ile bir 7z listelenir ve dosya çıkarılır.
/// Yalnızca Windows'ta ve 7-Zip kuruluysa çalışır (GitHub Actions windows-latest imajında kurulu).
/// </summary>
public class SevenZipEngineTests
{
    private static readonly SevenZipEngine Engine = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SevenZipExe =>
        Path.Combine(Path.GetDirectoryName(Engine.LibraryPath ?? "") ?? "", "7z.exe");

    private static void RequireSevenZip()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "7z.dll yalnızca Windows'ta yüklenir.");
        Assert.SkipUnless(Engine.IsAvailable, "7z.dll bulunamadı (7-Zip kurun veya AFUZIP_7Z_DLL ayarlayın).");
    }

    [Fact]
    public void Library_EnumeratesCoreFormats()
    {
        RequireSevenZip();
        var names = Engine.Formats.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in new[] { "7z", "zip", "Rar", "Rar5", "tar", "gzip", "xz", "Iso", "Cab", "wim" })
            Assert.Contains(expected, names);
        Assert.Contains(Engine.Formats, f => f.Name == "7z" && f.CanUpdate);
    }

    [Fact]
    public async Task ReadsZipCreatedByManagedEngine()
    {
        RequireSevenZip();
        using var temp = new TempDir();
        temp.WriteFile("kaynak/çiçek.txt", "Merhaba dünya");
        temp.WriteFile("kaynak/alt/veri.bin", ManagedEngineTests.RandomBytes(100_000));
        var zip = temp["test.zip"];
        await new ManagedEngine().CreateAsync(new CreateRequest(zip, [temp["kaynak"]], CompressionPreset.Share), ct: Ct);

        await using var reader = await Engine.OpenAsync(zip, OpenOptions.Default, Ct);
        Assert.Equal("zip", reader.FormatName, ignoreCase: true);
        Assert.Contains(reader.Entries, e => e.Path == "kaynak/çiçek.txt" && e.Crc is not null);

        var result = await reader.ExtractAsync(null, new ExtractionOptions(temp["çıktı"]), ct: Ct);
        Assert.True(result.Success, string.Join("; ", result.Failed));
        Assert.Equal(TempDir.Hashes(temp["kaynak"]), TempDir.Hashes(temp["çıktı/kaynak"]));
    }

    [Fact]
    public async Task ListsAndExtracts7z()
    {
        RequireSevenZip();
        Assert.SkipUnless(File.Exists(SevenZipExe), "7z.exe bulunamadı; 7z arşivi oluşturulamıyor.");

        using var temp = new TempDir();
        temp.WriteFile("kaynak/çiçek.txt", "Merhaba dünya — ŞĞÜÖÇİ ı");
        temp.WriteFile("kaynak/günlük.log", string.Concat(Enumerable.Repeat("satır\n", 20_000)));
        var archive = temp["test.7z"];
        RunSevenZip("a", "-t7z", "-mx=5", "-ms=on", archive, temp["kaynak"]);

        await using var reader = await Engine.OpenAsync(archive, OpenOptions.Default, Ct);
        Assert.Equal(ArchiveFormat.SevenZip, reader.Format);
        Assert.True(reader.IsSolid);
        var log = reader.Entries.Single(e => e.Name == "günlük.log");
        Assert.Equal(new FileInfo(temp["kaynak/günlük.log"]).Length, log.Size); // "satır\n" UTF-8'de 7 bayt
        Assert.StartsWith("LZMA2", log.Method);

        await using (var preview = await reader.OpenEntryAsync(reader.Entries.Single(e => e.Name == "çiçek.txt").Index, Ct))
            Assert.Equal("Merhaba dünya — ŞĞÜÖÇİ ı", await new StreamReader(preview).ReadToEndAsync(Ct));

        var result = await reader.ExtractAsync(null, new ExtractionOptions(temp["çıktı"]), ct: Ct);
        Assert.True(result.Success, string.Join("; ", result.Failed));
        Assert.Equal(TempDir.Hashes(temp["kaynak"]), TempDir.Hashes(temp["çıktı/kaynak"]));
    }

    [Fact]
    public async Task EncryptedHeaders_RequirePassword()
    {
        RequireSevenZip();
        Assert.SkipUnless(File.Exists(SevenZipExe), "7z.exe bulunamadı.");

        using var temp = new TempDir();
        temp.WriteFile("gizli.txt", "parola: hepsi bu kadar");
        var archive = temp["gizli.7z"];
        RunSevenZip("a", "-t7z", "-pTürkçe-Şifre", "-mhe=on", archive, temp["gizli.txt"]);

        var missing = await Assert.ThrowsAsync<ArchivePasswordException>(() => Engine.OpenAsync(archive, OpenOptions.Default, Ct));
        Assert.False(missing.WasProvided);

        var wrong = await Assert.ThrowsAsync<ArchivePasswordException>(() =>
            Engine.OpenAsync(archive, new OpenOptions(Passwords: new StaticPasswordProvider("yanlış")), Ct));
        Assert.True(wrong.WasProvided);

        await using var reader = await Engine.OpenAsync(archive, new OpenOptions(Passwords: new StaticPasswordProvider("Türkçe-Şifre")), Ct);
        var entry = Assert.Single(reader.Entries);
        Assert.True(entry.IsEncrypted);
        await using var stream = await reader.OpenEntryAsync(entry.Index, Ct);
        Assert.Equal("parola: hepsi bu kadar", await new StreamReader(stream).ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task WrongPassword_IsReportedPerEntryAndNotRemembered()
    {
        RequireSevenZip();
        Assert.SkipUnless(File.Exists(SevenZipExe), "7z.exe bulunamadı.");

        using var temp = new TempDir();
        temp.WriteFile("gizli.txt", "içerik");
        var archive = temp["gizli.7z"];
        RunSevenZip("a", "-t7z", "-pdoğru", archive, temp["gizli.txt"]); // başlıklar açık, içerik şifreli

        var passwords = new SequencePasswordProvider("yanlış", "doğru");
        await using var reader = await Engine.OpenAsync(archive, new OpenOptions(Passwords: passwords), Ct);
        var entry = Assert.Single(reader.Entries);

        // İlk deneme: yanlış parola girdi bazında hata olarak gelir, Extract yine de S_OK döner.
        var result = await reader.ExtractAsync(null, new ExtractionOptions(temp["çıktı"]), ct: Ct);
        var failure = Assert.Single(result.Failed);
        Assert.Equal("parola yanlış", failure.Reason);
        Assert.False(File.Exists(temp["çıktı/gizli.txt"]), "yarım kalan dosya silinmeli");

        // İkinci deneme: yanlış parola hatırlanmadığı için sağlayıcıya tekrar sorulur.
        await using var stream = await reader.OpenEntryAsync(entry.Index, Ct);
        Assert.Equal("içerik", await new StreamReader(stream).ReadToEndAsync(Ct));
        Assert.Equal(2, passwords.Calls);
    }

    [Fact]
    public async Task InvalidIndex_IsRejectedBeforeReachingNativeCode()
    {
        RequireSevenZip();
        using var temp = new TempDir();
        temp.WriteFile("kaynak/a.txt", "a");
        var zip = temp["test.zip"];
        await new ManagedEngine().CreateAsync(new CreateRequest(zip, [temp["kaynak"]], CompressionPreset.Share), ct: Ct);

        await using var reader = await Engine.OpenAsync(zip, OpenOptions.Default, Ct);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            reader.ExtractAsync([-1], new ExtractionOptions(temp["çıktı"]), ct: Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            reader.ExtractAsync([reader.Entries.Count], new ExtractionOptions(temp["çıktı"]), ct: Ct));
    }

    [Fact]
    public async Task SplitVolumes_AreJoinedAndUnwrapped()
    {
        RequireSevenZip();
        Assert.SkipUnless(File.Exists(SevenZipExe), "7z.exe bulunamadı.");

        using var temp = new TempDir();
        temp.WriteFile("kaynak/büyük.bin", ManagedEngineTests.RandomBytes(300_000));
        RunSevenZip("a", "-t7z", "-mx=0", "-v100k", temp["parçalı.7z"], temp["kaynak"]);
        Assert.True(File.Exists(temp["parçalı.7z.003"]));

        var opener = new ArchiveOpener(new EngineRouter([Engine]));
        await using var reader = await opener.OpenAsync(temp["parçalı.7z.001"], ct: Ct);

        Assert.Equal(ArchiveFormat.SevenZip, reader.Format);
        var result = await reader.ExtractAsync(null, new ExtractionOptions(temp["çıktı"]), ct: Ct);
        Assert.True(result.Success, string.Join("; ", result.Failed));
        Assert.Equal(TempDir.Hashes(temp["kaynak"]), TempDir.Hashes(temp["çıktı/kaynak"]));
    }

    [Fact]
    public async Task TarXz_IsUnwrappedToTarContents()
    {
        RequireSevenZip();
        Assert.SkipUnless(File.Exists(SevenZipExe), "7z.exe bulunamadı.");

        using var temp = new TempDir();
        temp.WriteFile("kaynak/a.txt", "a");
        temp.WriteFile("kaynak/alt/b.txt", "b");
        await new ManagedEngine().CreateAsync(new CreateRequest(temp["paket.tar"], [temp["kaynak"]], CompressionPreset.ForPath("x.tar")), ct: Ct);
        RunSevenZip("a", "-txz", temp["paket.tar.xz"], temp["paket.tar"]);

        var opener = new ArchiveOpener(new EngineRouter([Engine]));
        await using var reader = await opener.OpenAsync(temp["paket.tar.xz"], ct: Ct);

        Assert.Equal(ArchiveFormat.Tar, reader.Format);
        Assert.Equal("xz › tar", reader.FormatName);
        Assert.Contains(reader.Entries, e => e.Path == "kaynak/alt/b.txt");
    }

    [Fact]
    public async Task TestMode_DetectsCorruptionWithoutWriting()
    {
        RequireSevenZip();
        Assert.SkipUnless(File.Exists(SevenZipExe), "7z.exe bulunamadı.");

        using var temp = new TempDir();
        temp.WriteFile("kaynak/a.txt", string.Concat(Enumerable.Repeat("SAĞLAM VERİ ", 2000)));
        var archive = temp["test.7z"];
        RunSevenZip("a", "-t7z", "-mx=0", archive, temp["kaynak"]);

        await using (var healthy = await Engine.OpenAsync(archive, OpenOptions.Default, Ct))
            Assert.True((await healthy.TestAsync(ct: Ct)).Success);

        // Sıkıştırmasız verinin ortasında bir baytı boz.
        var bytes = File.ReadAllBytes(archive);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);

        await using var reader = await Engine.OpenAsync(archive, OpenOptions.Default, Ct);
        var result = await reader.TestAsync(ct: Ct);
        Assert.False(result.Success);
        Assert.Contains("CRC", Assert.Single(result.Failed).Reason);
    }

    [Fact]
    public async Task SolidArchive_OnlyRequestedEntriesAreCounted()
    {
        RequireSevenZip();
        Assert.SkipUnless(File.Exists(SevenZipExe), "7z.exe bulunamadı.");

        using var temp = new TempDir();
        temp.WriteFile("kaynak/a.txt", string.Concat(Enumerable.Repeat("aaaa ", 5000)));
        temp.WriteFile("kaynak/b.txt", string.Concat(Enumerable.Repeat("bbbb ", 5000)));
        temp.WriteFile("kaynak/c.txt", "son dosya");
        var archive = temp["solid.7z"];
        RunSevenZip("a", "-t7z", "-ms=on", archive, temp["kaynak"]);

        await using var reader = await Engine.OpenAsync(archive, OpenOptions.Default, Ct);
        Assert.True(reader.IsSolid);
        var last = reader.Entries.Single(e => e.Name == "c.txt").Index;

        // 7-Zip bloğun önceki girdilerini "atla" moduyla geçirir; bunlar sonuçlara karışmamalı.
        var test = await reader.TestAsync([last], ct: Ct);
        Assert.Equal(1, test.Tested);

        var extract = await reader.ExtractAsync([last], new ExtractionOptions(temp["çıktı"]), ct: Ct);
        Assert.Equal(1, extract.Extracted);
        Assert.Equal(["kaynak/c.txt"], TempDir.Hashes(temp["çıktı"]).Keys);

        await using var preview = await reader.OpenEntryAsync(last, Ct);
        Assert.Equal("son dosya", await new StreamReader(preview).ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task SolidArchive_ReportsBlockAndOpensMiddleEntry()
    {
        RequireSevenZip();
        Assert.SkipUnless(File.Exists(SevenZipExe), "7z.exe bulunamadı.");

        using var temp = new TempDir();
        temp.WriteFile("kaynak/1.txt", "bir");
        temp.WriteFile("kaynak/2.txt", "iki");
        temp.WriteFile("kaynak/3.txt", "üç");
        var archive = temp["blok.7z"];
        RunSevenZip("a", "-t7z", "-ms=on", archive, temp["kaynak"]);

        await using var reader = await Engine.OpenAsync(archive, OpenOptions.Default, Ct);
        var files = reader.Entries.Where(e => !e.IsDirectory).OrderBy(e => e.Name).ToList();
        Assert.All(files, f => Assert.Equal(files[0].Block, f.Block));
        Assert.NotNull(files[0].Block);

        // Solid bloğun ortasındaki dosya: önceki dosyalar da çözülür ama yalnızca istenen döner.
        await using var second = await reader.OpenEntryAsync(files[1].Index, Ct);
        Assert.Equal("iki", await new StreamReader(second).ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task TruncatedArchive_OpensWithWarning()
    {
        RequireSevenZip();

        using var temp = new TempDir();
        temp.WriteFile("kaynak/a.txt", "küçük");
        temp.WriteFile("kaynak/b.bin", ManagedEngineTests.RandomBytes(200_000));
        var tar = temp["yarım.tar"];
        await new ManagedEngine().CreateAsync(new CreateRequest(tar, [temp["kaynak"]], CompressionPreset.ForPath(tar)), ct: Ct);

        // Yarım indirilmiş dosya: son girdinin verisinin ortasından kes.
        using (var stream = File.OpenWrite(tar))
            stream.SetLength(stream.Length - 100_000);

        await using var reader = await Engine.OpenAsync(tar, OpenOptions.Default, Ct);
        Assert.Contains(reader.Warnings, w => w.Contains("beklenmedik", StringComparison.OrdinalIgnoreCase));
    }

    // --- Faz 3: oluşturma ve düzenleme --------------------------------------------------------------

    [Theory]
    [InlineData("çıktı.7z", ArchiveFormat.SevenZip)]
    [InlineData("çıktı.zip", ArchiveFormat.Zip)]
    [InlineData("çıktı.tar", ArchiveFormat.Tar)]
    [InlineData("çıktı.tar.gz", ArchiveFormat.Tar)]
    public async Task Create_RoundTrips(string name, ArchiveFormat expected)
    {
        RequireSevenZip();
        using var temp = new TempDir();
        temp.WriteFile("kaynak/çiçek.txt", "Merhaba dünya — ŞĞÜÖÇİ ı");
        temp.WriteFile("kaynak/alt/veri.bin", ManagedEngineTests.RandomBytes(300_000));

        var archive = temp[name];
        await Engine.CreateAsync(new CreateRequest(archive, [temp["kaynak"]], CompressionPreset.ForPath(archive)), ct: Ct);

        var opener = new ArchiveOpener(new EngineRouter([Engine]));
        await using var reader = await opener.OpenAsync(archive, ct: Ct);
        Assert.Equal(expected, reader.Format);
        var result = await reader.ExtractAsync(null, new ExtractionOptions(temp["çıkan"]), ct: Ct);
        Assert.True(result.Success, string.Join("; ", result.Failed));
        Assert.Equal(TempDir.Hashes(temp["kaynak"]), TempDir.Hashes(temp["çıkan/kaynak"]));
    }

    [Fact]
    public async Task Create_EncryptedHeaders_HideNamesWithoutPassword()
    {
        RequireSevenZip();
        using var temp = new TempDir();
        temp.WriteFile("gizli/plan.txt", "çok gizli");
        var archive = temp["gizli.7z"];
        await Engine.CreateAsync(new CreateRequest(archive, [temp["gizli"]], CompressionPreset.Balanced, Password: "Parola1!",
            EncryptHeaders: true), ct: Ct);

        await Assert.ThrowsAsync<ArchivePasswordException>(() => Engine.OpenAsync(archive, OpenOptions.Default, Ct));

        await using var reader = await Engine.OpenAsync(archive, new OpenOptions(Passwords: new StaticPasswordProvider("Parola1!")), Ct);
        var entry = reader.Entries.Single(e => e.Name == "plan.txt");
        Assert.True(entry.IsEncrypted);
        await using var stream = await reader.OpenEntryAsync(entry.Index, Ct);
        Assert.Equal("çok gizli", await new StreamReader(stream).ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task Create_ZipAes_IsEncrypted()
    {
        RequireSevenZip();
        using var temp = new TempDir();
        temp.WriteFile("a.txt", "zip içinde AES");
        var archive = temp["aes.zip"];
        await Engine.CreateAsync(new CreateRequest(archive, [temp["a.txt"]], CompressionPreset.Share, Password: "p"), ct: Ct);

        await using var reader = await Engine.OpenAsync(archive, new OpenOptions(Passwords: new StaticPasswordProvider("p")), Ct);
        Assert.True(reader.Entries.Single().IsEncrypted);
        Assert.Contains("AES", reader.Entries.Single().Method);
    }

    [Fact]
    public async Task Create_Volumes_OpenFromFirstPart()
    {
        RequireSevenZip();
        using var temp = new TempDir();
        temp.WriteFile("kaynak/büyük.bin", ManagedEngineTests.RandomBytes(400_000));
        var archive = temp["bölünmüş.7z"];
        await Engine.CreateAsync(new CreateRequest(archive, [temp["kaynak"]], CompressionPreset.Balanced with { Level = 0 },
            VolumeSize: 128 * 1024), ct: Ct);

        Assert.False(File.Exists(archive));
        Assert.True(File.Exists(temp["bölünmüş.7z.004"]));

        var opener = new ArchiveOpener(new EngineRouter([Engine]));
        await using var reader = await opener.OpenAsync(temp["bölünmüş.7z.001"], ct: Ct);
        var result = await reader.ExtractAsync(null, new ExtractionOptions(temp["çıkan"]), ct: Ct);
        Assert.True(result.Success, string.Join("; ", result.Failed));
        Assert.Equal(TempDir.Hashes(temp["kaynak"]), TempDir.Hashes(temp["çıkan/kaynak"]));
    }

    [Fact]
    public async Task Update_AddDeleteRename_KeepsOtherEntriesIntact()
    {
        RequireSevenZip();
        using var temp = new TempDir();
        temp.WriteFile("proje/README.md", "# Proje");
        temp.WriteFile("proje/src/Oyuncu.cs", "class Oyuncu {}");
        temp.WriteFile("proje/eski.log", "sil beni");
        temp.WriteFile("yeni/Düşman.cs", "class Düşman {}");
        var archive = temp["proje.7z"];
        await Engine.CreateAsync(new CreateRequest(archive, [temp["proje"]], CompressionPreset.Balanced), ct: Ct);

        var update = new ArchiveUpdate
        {
            Delete = ["proje/eski.log"],
            Rename = new Dictionary<string, string> { ["proje/src"] = "kaynak" },
            Add = [temp["yeni/Düşman.cs"]],
            AddToFolder = "proje/kaynak",
        };
        using (var pending = await Engine.PrepareUpdateAsync(archive, update, OpenOptions.Default, ct: Ct))
            pending.Commit();

        await using var reader = await Engine.OpenAsync(archive, OpenOptions.Default, Ct);
        // Ordinal: varsayılan karşılaştırıcı kültüre bağlı ve büyük/küçük harfi yok sayar ("kaynak" < "README").
        var paths = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Path).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["proje/README.md", "proje/kaynak/Düşman.cs", "proje/kaynak/Oyuncu.cs"], paths);
        Assert.True((await reader.TestAsync(ct: Ct)).Success);
    }

    [Fact]
    public async Task Update_Abandoned_LeavesOriginalUntouched()
    {
        RequireSevenZip();
        using var temp = new TempDir();
        temp.WriteFile("a.txt", "a");
        var archive = temp["sabit.7z"];
        await Engine.CreateAsync(new CreateRequest(archive, [temp["a.txt"]], CompressionPreset.Balanced), ct: Ct);
        var before = File.ReadAllBytes(archive);

        using (await Engine.PrepareUpdateAsync(archive, new ArchiveUpdate { Delete = ["a.txt"] }, OpenOptions.Default, ct: Ct))
        {
            // Commit edilmedi.
        }

        Assert.Equal(before, File.ReadAllBytes(archive));
        Assert.Single(Directory.GetFiles(temp.Path, "sabit.7z*"));
    }

    [Fact]
    public async Task Update_ReadOnlyFormat_IsRejected()
    {
        RequireSevenZip();
        using var temp = new TempDir();
        temp.WriteFile("a.bin", ManagedEngineTests.RandomBytes(200_000));
        // Ciltli arşivin ilk parçası 7-Zip'te "Split" işleyicisiyle açılır; o biçim yazılamaz.
        await Engine.CreateAsync(new CreateRequest(temp["c.7z"], [temp["a.bin"]], CompressionPreset.Balanced with { Level = 0 },
            VolumeSize: 64 * 1024), ct: Ct);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Engine.PrepareUpdateAsync(temp["c.7z.001"], new ArchiveUpdate { Delete = ["a.bin"] }, OpenOptions.Default, ct: Ct));
    }

    /// <summary>
    /// Uçtan uca: oluştur → sil → yeniden adlandır → ekle, iç içe klasörler ve çok dosyayla (zip'in çok iş
    /// parçacıklı yolu dahil); sonuç hem kendi okuyucumuzla hem 7z.exe ile test edilir.
    /// </summary>
    [Theory]
    [InlineData("tur.7z")]
    [InlineData("tur.zip")]
    [InlineData("tur.tar")]
    public async Task EditRoundTrip_ProducesValidArchive(string name)
    {
        RequireSevenZip();
        using var temp = new TempDir();
        for (var i = 0; i < 12; i++)
            temp.WriteFile($"proje/src/alt{i % 3}/dosya{i}.cs", string.Concat(Enumerable.Repeat($"satır {i}\n", 500)));
        temp.WriteFile("proje/büyük.bin", ManagedEngineTests.RandomBytes(400_000));
        temp.WriteFile("ek/yeni.txt", "eklendi");
        Directory.CreateDirectory(temp["ek/boş klasör"]);

        var archive = temp[name];
        await Engine.CreateAsync(new CreateRequest(archive, [temp["proje"]], CompressionPreset.ForPath(archive)), ct: Ct);

        foreach (var update in new ArchiveUpdate[]
        {
            new() { Delete = ["proje/src/alt0"] },
            new() { Rename = new Dictionary<string, string> { ["proje/src"] = "kaynak" } },
            new() { Add = [temp["ek"]], AddToFolder = "proje" },
        })
        {
            using var pending = await Engine.PrepareUpdateAsync(archive, update, OpenOptions.Default, ct: Ct);
            pending.Commit();
        }

        await using (var reader = await Engine.OpenAsync(archive, OpenOptions.Default, Ct))
        {
            var files = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Path).ToList();
            Assert.Equal(8 + 1 + 1, files.Count); // 12 - 4 silinen + büyük.bin + yeni.txt
            Assert.DoesNotContain(files, f => f.Contains("alt0") || f.StartsWith("proje/src/"));
            Assert.Contains("proje/ek/yeni.txt", files);
            Assert.Contains(reader.Entries, e => e.IsDirectory && e.Path.TrimEnd('/') == "proje/ek/boş klasör");
            var test = await reader.TestAsync(ct: Ct);
            Assert.True(test.Success, string.Join("; ", test.Failed));
        }

        if (File.Exists(SevenZipExe))
            RunSevenZip("t", archive);
    }

    [Fact]
    public async Task EditEncrypted_RequiresPassword_AndEncryptsNewFiles()
    {
        RequireSevenZip();
        using var temp = new TempDir();
        temp.WriteFile("gizli/a.txt", string.Concat(Enumerable.Repeat("gizli veri ", 2000)));
        temp.WriteFile("gizli/b.txt", "ikinci");
        temp.WriteFile("yeni.txt", "yeni gizli");
        var archive = temp["şifreli.7z"];
        await Engine.CreateAsync(new CreateRequest(archive, [temp["gizli"]], CompressionPreset.Balanced, Password: "doğru"), ct: Ct);

        // Yanlış parola düzenlemeden önce yakalanır; arşive karışık parola girmez.
        await Assert.ThrowsAsync<ArchivePasswordException>(() =>
            Engine.PrepareUpdateAsync(archive, new ArchiveUpdate { Add = [temp["yeni.txt"]], Password = "yanlış" }, OpenOptions.Default, ct: Ct));

        // Solid blokta silme eski veriyi yeniden paketlemeyi gerektirir; parola sağlayıcıdan alınır.
        var withPassword = new OpenOptions(Passwords: new StaticPasswordProvider("doğru"));
        using (var pending = await Engine.PrepareUpdateAsync(archive, new ArchiveUpdate { Delete = ["gizli/b.txt"], Add = [temp["yeni.txt"]] },
                   withPassword, ct: Ct))
            pending.Commit();

        await using var reader = await Engine.OpenAsync(archive, withPassword, Ct);
        Assert.All(reader.Entries.Where(e => !e.IsDirectory), e => Assert.True(e.IsEncrypted, e.Path));
        Assert.DoesNotContain(reader.Entries, e => e.Name == "b.txt");
        Assert.True((await reader.TestAsync(ct: Ct)).Success);
    }

    private sealed class SequencePasswordProvider(params string[] passwords) : IPasswordProvider
    {
        public int Calls { get; private set; }

        public ValueTask<string?> GetPasswordAsync(string archivePath, CancellationToken ct = default) =>
            ValueTask.FromResult<string?>(passwords[Math.Min(Calls++, passwords.Length - 1)]);
    }

    private static void RunSevenZip(params string[] args)
    {
        var info = new ProcessStartInfo(SevenZipExe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}

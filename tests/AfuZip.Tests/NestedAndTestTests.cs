using System.IO.Compression;
using System.Text;
using AfuZip.Engine.Managed;

namespace AfuZip.Tests;

public class ArchiveTestingTests
{
    private readonly ManagedEngine _engine = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("sağlam.zip")]
    [InlineData("sağlam.tar.gz")]
    [InlineData("sağlam.tar")]
    public async Task HealthyArchive_PassesTest(string name)
    {
        using var temp = new TempDir();
        temp.WriteFile("kaynak/a.txt", "birinci");
        temp.WriteFile("kaynak/alt/b.bin", ManagedEngineTests.RandomBytes(50_000));
        var archive = temp[name];
        await _engine.CreateAsync(new CreateRequest(archive, [temp["kaynak"]], CompressionPreset.ForPath(archive)), ct: Ct);

        await using var reader = await _engine.OpenAsync(archive, OpenOptions.Default, Ct);
        var result = await reader.TestAsync(ct: Ct);

        Assert.True(result.Success, string.Join("; ", result.Failed));
        Assert.Equal(2, result.Tested);
    }

    [Fact]
    public async Task CorruptedZipEntry_IsReportedWithoutStoppingOthers()
    {
        using var temp = new TempDir();
        var zip = temp["bozuk.zip"];
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("iyi.txt", "SAĞLAM-VERİ-0001"), ("kötü.txt", "BOZULACAK-VERİ-42") })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.NoCompression).Open(), Encoding.UTF8);
                writer.Write(text);
            }
        }

        // Sıkıştırmasız girdinin verisinde tek bir baytı değiştir.
        var bytes = File.ReadAllBytes(zip);
        var marker = Encoding.UTF8.GetBytes("BOZULACAK");
        var at = bytes.AsSpan().IndexOf(marker);
        Assert.True(at > 0);
        bytes[at] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);

        await using var reader = await _engine.OpenAsync(zip, OpenOptions.Default, Ct);
        var result = await reader.TestAsync(ct: Ct);

        var failure = Assert.Single(result.Failed);
        Assert.Equal("kötü.txt", failure.EntryPath);
        Assert.Equal(1, result.Tested);
    }
}

public class NestedArchiveTests
{
    private readonly ManagedEngine _engine = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ArchiveOpener Opener => new(new EngineRouter([_engine]));

    [Fact]
    public async Task ZipInsideZip_OpensAndCleansUpTempFiles()
    {
        using var temp = new TempDir();
        temp.WriteFile("iç/derin.txt", "en içteki dosya");
        await _engine.CreateAsync(new CreateRequest(temp["iç.zip"], [temp["iç"]], CompressionPreset.Share), ct: Ct);
        await _engine.CreateAsync(new CreateRequest(temp["dış.zip"], [temp["iç.zip"]], CompressionPreset.Share), ct: Ct);

        await using var outer = await Opener.OpenAsync(temp["dış.zip"], ct: Ct);
        var innerEntry = Assert.Single(outer.Entries);
        Assert.True(NestedArchive.LooksLikeArchive(innerEntry));

        string tempPath;
        await using (var inner = await Opener.OpenNestedAsync(outer, innerEntry.Index, ct: Ct))
        {
            Assert.Equal("dış.zip › iç.zip", inner.DisplayName);
            Assert.Equal("zip › zip", inner.FormatName);
            var file = inner.Entries.Single(e => e.Name == "derin.txt");
            await using var stream = await inner.OpenEntryAsync(file.Index, Ct);
            Assert.Equal("en içteki dosya", await new StreamReader(stream).ReadToEndAsync(Ct));
            tempPath = inner.ArchivePath;
            Assert.True(File.Exists(tempPath));
        }

        Assert.False(File.Exists(tempPath), "geçici iç arşiv silinmeli");
        Assert.NotEmpty(outer.Entries); // dış arşiv açık kalır
    }

    [Fact]
    public async Task SingleStreamWrapper_IsUnwrappedAutomatically()
    {
        using var temp = new TempDir();
        temp.WriteFile("veri/rapor.txt", "çeyrek sonuçları");
        await _engine.CreateAsync(new CreateRequest(temp["paket.zip"], [temp["veri"]], CompressionPreset.Share), ct: Ct);

        // paket.zip.gz: tek dosyalık gzip içinde zip
        await using (var source = File.OpenRead(temp["paket.zip"]))
        await using (var target = File.Create(temp["paket.zip.gz"]))
        await using (var gzip = new GZipStream(target, CompressionLevel.Optimal))
            await source.CopyToAsync(gzip, Ct);

        await using var reader = await Opener.OpenAsync(temp["paket.zip.gz"], ct: Ct);

        Assert.Equal(ArchiveFormat.Zip, reader.Format);
        Assert.Equal("paket.zip.gz › paket.zip", reader.DisplayName);
        Assert.Contains(reader.Entries, e => e.Path == "veri/rapor.txt");
    }

    [Fact]
    public async Task WrapperAroundNonArchive_IsShownAsIs()
    {
        using var temp = new TempDir();
        await using (var target = File.Create(temp["notlar.txt.gz"]))
        await using (var gzip = new GZipStream(target, CompressionLevel.Optimal))
            await gzip.WriteAsync("düz metin"u8.ToArray(), Ct);

        await using var reader = await Opener.OpenAsync(temp["notlar.txt.gz"], ct: Ct);
        Assert.Equal(ArchiveFormat.GZip, reader.Format);
        Assert.Equal("notlar.txt", Assert.Single(reader.Entries).Path);
    }
}

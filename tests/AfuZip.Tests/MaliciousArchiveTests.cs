using System.Formats.Tar;
using System.IO.Compression;
using AfuZip.Engine.Managed;

namespace AfuZip.Tests;

/// <summary>Kötü niyetli korpus: hepsi engellenmeli, hiçbiri hedef klasör dışına yazmamalı.</summary>
public class MaliciousArchiveTests
{
    private readonly ManagedEngine _engine = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CreateZip(TempDir temp, string name, params (string Entry, string Content)[] entries)
    {
        var path = temp[name];
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
            writer.Write(content);
        }
        return path;
    }

    [Theory]
    [InlineData("../kaçış.txt")]
    [InlineData("alt/../../kaçış.txt")]
    [InlineData("..\\kaçış.txt")]
    public async Task ZipSlip_IsBlockedAndNothingEscapes(string evilEntry)
    {
        using var temp = new TempDir();
        var zip = CreateZip(temp, "zipslip.zip", ("normal.txt", "iyi"), (evilEntry, "kötü"));

        await using var reader = await _engine.OpenAsync(zip, OpenOptions.Default, Ct);
        var result = await reader.ExtractAsync(null, new ExtractionOptions(temp["hedef/iç"]), ct: Ct);

        Assert.Equal(1, result.Extracted);
        var blocked = Assert.Single(result.Blocked);
        Assert.Contains("..", blocked.Reason);
        Assert.False(File.Exists(temp["hedef/kaçış.txt"]));
        Assert.False(File.Exists(temp["kaçış.txt"]));
        Assert.True(File.Exists(temp["hedef/iç/normal.txt"]));
    }

    [Fact]
    public async Task AbsoluteAndDevicePaths_AreRewrittenInsideTarget()
    {
        using var temp = new TempDir();
        var zip = CreateZip(temp, "yollar.zip",
            ("/etc/passwd", "a"),
            ("C:/Windows/win.ini", "b"),
            ("CON", "c"),
            ("not.txt:Zone.Identifier", "d"));

        await using var reader = await _engine.OpenAsync(zip, OpenOptions.Default, Ct);
        var result = await reader.ExtractAsync(null, new ExtractionOptions(temp["hedef"]), ct: Ct);

        Assert.Equal(4, result.Extracted);
        Assert.True(File.Exists(temp["hedef/etc/passwd"]));
        Assert.True(File.Exists(Path.Combine(temp["hedef"], "C_", "Windows", "win.ini")));
        Assert.True(File.Exists(temp["hedef/_CON"]));
        Assert.True(File.Exists(temp["hedef/not.txt_Zone.Identifier"]));
    }

    [Fact]
    public async Task Symlinks_AreNotCreatedByDefault()
    {
        using var temp = new TempDir();
        var tar = temp["link.tar"];
        await using (var file = File.Create(tar))
        await using (var writer = new TarWriter(file, TarEntryFormat.Pax))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, "kötü-link") { LinkName = "/etc" }, Ct);
            var regular = new PaxTarEntry(TarEntryType.RegularFile, "kötü-link/passwd") { DataStream = new MemoryStream("x"u8.ToArray()) };
            await writer.WriteEntryAsync(regular, Ct);
        }

        await using var reader = await _engine.OpenAsync(tar, OpenOptions.Default, Ct);
        var result = await reader.ExtractAsync(null, new ExtractionOptions(temp["hedef"]), ct: Ct);

        Assert.Contains(result.Blocked, b => b.EntryPath == "kötü-link");
        var linkPath = temp["hedef/kötü-link"];
        Assert.False(File.Exists(linkPath) && new FileInfo(linkPath).LinkTarget is not null);
        Assert.Null(new DirectoryInfo(linkPath).LinkTarget);
    }

    [Fact]
    public async Task ZipBomb_IsStoppedByExpansionLimit()
    {
        using var temp = new TempDir();
        var bomb = temp["bomb.zip"];
        using (var zip = ZipFile.Open(bomb, ZipArchiveMode.Create))
        await using (var entry = zip.CreateEntry("sıfırlar.bin", CompressionLevel.SmallestSize).Open())
        {
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 40; i++)
                await entry.WriteAsync(zeros, Ct);
        }

        var limits = new ExtractionLimits(MaxExpansionRatio: 100, RatioCheckThresholdBytes: 1024 * 1024);
        await using var reader = await _engine.OpenAsync(bomb, OpenOptions.Default, Ct);

        var ex = await Assert.ThrowsAsync<ExtractionLimitExceededException>(() =>
            reader.ExtractAsync(null, new ExtractionOptions(temp["hedef"]) { Limits = limits }, ct: Ct));

        Assert.Contains("zip bomb", ex.Message);
        Assert.False(File.Exists(temp["hedef/sıfırlar.bin"]), "yarım kalan dosya silinmeli");
    }

    [Fact]
    public async Task TotalSizeLimit_IsEnforced()
    {
        using var temp = new TempDir();
        var zip = temp["büyük.zip"];
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        await using (var entry = archive.CreateEntry("veri.bin", CompressionLevel.NoCompression).Open())
            await entry.WriteAsync(ManagedEngineTests.RandomBytes(3 * 1024 * 1024), Ct);

        var limits = new ExtractionLimits(MaxTotalBytes: 1024 * 1024);
        await using var reader = await _engine.OpenAsync(zip, OpenOptions.Default, Ct);

        await Assert.ThrowsAsync<ExtractionLimitExceededException>(() =>
            reader.ExtractAsync(null, new ExtractionOptions(temp["hedef"]) { Limits = limits }, ct: Ct));
    }
}

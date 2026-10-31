using System.IO.Compression;
using System.Text;
using AfuZip.Engine.Managed;

namespace AfuZip.Tests;

public class ManagedEngineTests
{
    private readonly ManagedEngine _engine = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string LogContent = string.Concat(Enumerable.Repeat("tekrar eden satır\n", 5000));

    private static void CreateSampleTree(TempDir temp)
    {
        temp.WriteFile("kaynak/çiçek.txt", "Merhaba dünya — ŞĞÜÖÇİ ı");
        temp.WriteFile("kaynak/alt klasör/günlük.log", LogContent);
        temp.WriteFile("kaynak/rastgele.bin", RandomBytes(200_000));
        Directory.CreateDirectory(temp["kaynak/boş klasör"]);
    }

    [Theory]
    [InlineData("test.zip")]
    [InlineData("test.tar")]
    [InlineData("test.tar.gz")]
    public async Task CreateListExtract_RoundTripsContent(string archiveName)
    {
        using var temp = new TempDir();
        CreateSampleTree(temp);
        var archive = temp[archiveName];

        await _engine.CreateAsync(new CreateRequest(archive, [temp["kaynak"]], CompressionPreset.ForPath(archive)), ct: Ct);

        await using var reader = await _engine.OpenAsync(archive, OpenOptions.Default, Ct);
        Assert.Contains(reader.Entries, e => e.Path == "kaynak/alt klasör/günlük.log" && e.Size == Encoding.UTF8.GetByteCount(LogContent));
        Assert.Contains(reader.Entries, e => e.IsDirectory && e.Path.TrimEnd('/') == "kaynak/boş klasör");

        var result = await reader.ExtractAsync(null, new ExtractionOptions(temp["çıktı"]), ct: Ct);

        Assert.True(result.Success, string.Join("; ", result.Failed));
        Assert.Empty(result.Blocked);
        Assert.Equal(3, result.Extracted);
        Assert.Equal(TempDir.Hashes(temp["kaynak"]), TempDir.Hashes(temp["çıktı/kaynak"]));
        Assert.True(Directory.Exists(temp["çıktı/kaynak/boş klasör"]));
    }

    [Fact]
    public async Task OpenEntry_ReturnsRewoundSeekableStream()
    {
        using var temp = new TempDir();
        CreateSampleTree(temp);
        var archive = temp["test.tar.gz"];
        await _engine.CreateAsync(new CreateRequest(archive, [temp["kaynak"]], CompressionPreset.ForPath(archive)), ct: Ct);

        await using var reader = await _engine.OpenAsync(archive, OpenOptions.Default, Ct);
        Assert.True(reader.IsSolid);
        var entry = reader.Entries.Single(e => e.Name == "çiçek.txt");

        await using var stream = await reader.OpenEntryAsync(entry.Index, Ct);
        Assert.True(stream.CanSeek);
        Assert.Equal(0, stream.Position);
        Assert.Equal("Merhaba dünya — ŞĞÜÖÇİ ı", await new StreamReader(stream).ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task SelectedIndices_ExtractOnlyThoseEntries()
    {
        using var temp = new TempDir();
        CreateSampleTree(temp);
        var archive = temp["test.zip"];
        await _engine.CreateAsync(new CreateRequest(archive, [temp["kaynak"]], CompressionPreset.Share), ct: Ct);

        await using var reader = await _engine.OpenAsync(archive, OpenOptions.Default, Ct);
        var wanted = reader.Entries.Single(e => e.Name == "rastgele.bin").Index;
        var result = await reader.ExtractAsync([wanted], new ExtractionOptions(temp["çıktı"]), ct: Ct);

        Assert.Equal(1, result.Extracted);
        Assert.True(File.Exists(temp["çıktı/kaynak/rastgele.bin"]));
        Assert.False(File.Exists(temp["çıktı/kaynak/çiçek.txt"]));
    }

    [Theory]
    [InlineData(OverwritePolicy.Skip, "eski", 1)]
    [InlineData(OverwritePolicy.Overwrite, "yeni", 0)]
    [InlineData(OverwritePolicy.Rename, "eski", 0)]
    public async Task OverwritePolicy_IsApplied(OverwritePolicy policy, string expectedOriginal, int expectedSkipped)
    {
        using var temp = new TempDir();
        temp.WriteFile("kaynak/a.txt", "yeni");
        var archive = temp["test.zip"];
        await _engine.CreateAsync(new CreateRequest(archive, [temp["kaynak/a.txt"]], CompressionPreset.Share), ct: Ct);
        temp.WriteFile("çıktı/a.txt", "eski");

        await using var reader = await _engine.OpenAsync(archive, OpenOptions.Default, Ct);
        var result = await reader.ExtractAsync(null, new ExtractionOptions(temp["çıktı"]) { Overwrite = policy }, ct: Ct);

        Assert.Equal(expectedOriginal, File.ReadAllText(temp["çıktı/a.txt"]));
        Assert.Equal(expectedSkipped, result.Skipped);
        if (policy == OverwritePolicy.Rename)
            Assert.Equal("yeni", File.ReadAllText(temp["çıktı/a (2).txt"]));
    }

    private static readonly byte[] Csv = "ad;soyad\nAyşe;Yılmaz\n"u8.ToArray();

    [Fact]
    public async Task SingleGZipFile_IsReadAsOneEntry()
    {
        using var temp = new TempDir();
        var gz = temp["rapor.csv.gz"];
        await using (var file = File.Create(gz))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            await gzip.WriteAsync(Csv, Ct);

        await using var reader = await _engine.OpenAsync(gz, OpenOptions.Default, Ct);
        var entry = Assert.Single(reader.Entries);
        Assert.Equal("rapor.csv", entry.Path);
        Assert.Equal(Csv.Length, entry.Size); // "ş" ve "ı" UTF-8'de ikişer bayt
    }

    [Fact]
    public async Task Router_FallsBackToManagedEngineWhenSevenZipIsMissing()
    {
        using var temp = new TempDir();
        temp.WriteFile("kaynak/a.txt", "a");
        var archive = temp["test.zip"];
        await _engine.CreateAsync(new CreateRequest(archive, [temp["kaynak"]], CompressionPreset.Share), ct: Ct);

        var router = new EngineRouter([new UnavailableEngine(), _engine]);
        await using var reader = await router.OpenAsync(archive, ct: Ct);
        Assert.Equal("managed", reader.EngineName);
        Assert.Equal(ArchiveFormat.Zip, reader.Format);
    }

    internal static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    private sealed class UnavailableEngine : IArchiveEngine
    {
        public string Name => "yok";
        public bool IsAvailable => false;
        public bool Supports(ArchiveFormat format, Capability needed) => true;
        public Task<IArchiveReader> OpenAsync(string archivePath, OpenOptions options, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task CreateAsync(CreateRequest request, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) => throw new InvalidOperationException();
    }
}

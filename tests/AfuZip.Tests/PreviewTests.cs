using System.Text;
using AfuZip.Core.Preview;
using AfuZip.Engine.Managed;

namespace AfuZip.Tests;

public class PreviewClassifierTests
{
    private static PreviewType Classify(string name, byte[] head) => PreviewClassifier.Classify(name, head);
    private static PreviewType Classify(string name, string text) => Classify(name, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void SignatureWinsOverWrongExtension()
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
        Assert.Equal(PreviewKind.Image, Classify("ekran görüntüsü.txt", png).Kind);
        Assert.Equal("image/png", Classify("adsız", png).MimeType);
    }

    [Theory]
    [InlineData("Oyuncu.cs", "public class Oyuncu {}", PreviewKind.Text, "csharp")]
    [InlineData("app.ts", "export const a = 1;", PreviewKind.Text, "typescript")]
    [InlineData("Lit.shader", "Shader \"Custom/Lit\" {}", PreviewKind.Text, "cpp")]
    [InlineData("ayar.json", "{\"a\": 1}", PreviewKind.Json, "json")]
    [InlineData("Proje.csproj", "<Project />", PreviewKind.Xml, "xml")]
    [InlineData("README.md", "# Başlık", PreviewKind.Markdown, "markdown")]
    [InlineData("logo.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>", PreviewKind.Svg, "xml")]
    [InlineData("Dockerfile", "FROM mcr.microsoft.com/dotnet/runtime:10.0", PreviewKind.Text, "dockerfile")]
    [InlineData("kur", "#!/usr/bin/env python3\nprint('merhaba')", PreviewKind.Text, "python")]
    [InlineData("baslat", "#!/bin/bash\necho hi", PreviewKind.Text, "shell")]
    [InlineData("veri", "{\"oyuncular\": []}", PreviewKind.Json, "json")]
    [InlineData("LICENSE", "MIT License", PreviewKind.Text, "plaintext")]
    public void TextualFiles(string name, string content, PreviewKind kind, string language)
    {
        var type = Classify(name, content);
        Assert.Equal(kind, type.Kind);
        Assert.Equal(language, type.Language);
        Assert.True(type.IsWeb);
    }

    [Fact]
    public void BinaryContentWithTextExtension_FallsBackToHex()
    {
        var bytes = new byte[512];
        new Random(1).NextBytes(bytes);
        bytes[10] = 0;
        Assert.Equal(PreviewKind.Binary, Classify("not.txt", bytes).Kind);
    }

    [Fact]
    public void MediaAndDocuments()
    {
        Assert.Equal(PreviewKind.Pdf, Classify("x.bin", "%PDF-1.7\n").Kind);
        Assert.Equal(PreviewKind.Video, Classify("klip", Ftyp("isom")).Kind);
        Assert.Equal(PreviewKind.Audio, Classify("şarkı", Ftyp("M4A ")).Kind);
        Assert.Equal("image/heic", Classify("IMG_0001", Ftyp("heic")).MimeType);
        Assert.Equal(PreviewKind.Audio, Classify("a.mp3", "ID3\u0004\0\0\0\0").Kind);
        Assert.Equal(PreviewKind.Video, Classify("film.mkv", [0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0]).Kind);

        // .ts TypeScript'tir; içerik MPEG-TS ise video.
        var transport = new byte[400];
        transport[0] = transport[188] = transport[376] = 0x47;
        Assert.Equal(PreviewKind.Video, Classify("kayıt.ts", transport).Kind);

        static byte[] Ftyp(string brand) => [0, 0, 0, 0x18, .. "ftyp"u8, .. Encoding.ASCII.GetBytes(brand), 0, 0, 0, 0];
    }

    [Fact]
    public void NestedArchive_IsRecognizedBySignature()
    {
        byte[] zip = [(byte)'P', (byte)'K', 3, 4, 20, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(PreviewKind.Archive, Classify("paket.dat", zip).Kind);
    }

    [Fact]
    public void UnknownBinary_IsHex()
    {
        byte[] bytes = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00];
        Assert.Equal(PreviewKind.Binary, Classify("oyun.exe", bytes).Kind);
    }
}

public class TextDecoderTests
{
    // TextDecoder kod sayfası sağlayıcısını kaydeder; 1254 oradan alınır.
    private static Encoding Windows1254 => TextDecoder.Choices[1];

    [Fact]
    public void Utf8_IsDetected()
    {
        var result = TextDecoder.Decode(Encoding.UTF8.GetBytes("Çağrı ışığı söndü"));
        Assert.Equal(65001, result.Encoding.CodePage);
        Assert.Equal("Çağrı ışığı söndü", result.Text);
    }

    [Fact]
    public void Bom_IsStrippedFromText()
    {
        var result = TextDecoder.Decode([0xEF, 0xBB, 0xBF, .. "merhaba"u8]);
        Assert.Equal("merhaba", result.Text);
    }

    [Fact]
    public void InvalidUtf8_FallsBackToAnsi_AndCanBeForced()
    {
        var bytes = Windows1254.GetBytes("Çağrı ışığı söndü");
        var detected = TextDecoder.Decode(bytes);
        Assert.NotEqual(65001, detected.Encoding.CodePage);
        Assert.Equal(TextDecoder.SystemAnsi.CodePage, detected.Encoding.CodePage);

        Assert.Equal("Çağrı ışığı söndü", TextDecoder.Decode(bytes, Windows1254).Text);
    }

    [Fact]
    public void Utf16WithoutBom_IsDetected()
    {
        var bytes = Encoding.Unicode.GetBytes("Windows kayıt dosyası\r\n[HKEY_LOCAL_MACHINE]");
        Assert.True(TextDecoder.LooksLikeText(bytes));
        var result = TextDecoder.Decode(bytes);
        Assert.Equal(1200, result.Encoding.CodePage);
        Assert.StartsWith("Windows kayıt", result.Text);
    }

    [Fact]
    public async Task TruncatedRead_DropsHalfCharacter()
    {
        // "aş" = 61 C5 9F; 2 baytta kesince yarım 'ş' atılmalı, U+FFFD görünmemeli.
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("aşk"));
        var result = await TextDecoder.ReadAsync(stream, maxBytes: 2, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Truncated);
        Assert.Equal(65001, result.Encoding.CodePage);
        Assert.Equal("a", result.Text);
    }

    [Fact]
    public async Task SmallFile_IsNotTruncated()
    {
        var result = await TextDecoder.ReadAsync(new MemoryStream("kısa"u8.ToArray()), ct: TestContext.Current.CancellationToken);
        Assert.False(result.Truncated);
        Assert.Equal("kısa", result.Text);
    }

    [Fact]
    public void ControlHeavyContent_IsNotText()
    {
        var bytes = Enumerable.Range(1, 200).Select(i => (byte)(i % 8 + 1)).ToArray();
        Assert.False(TextDecoder.LooksLikeText(bytes));
        Assert.True(TextDecoder.LooksLikeText("\u001b[32mOK\u001b[0m\tsatır\n"u8));
    }
}

public class HexDocumentTests
{
    [Fact]
    public void FormatsRows()
    {
        var bytes = "MZ"u8.ToArray().Concat(Enumerable.Range(0, 30).Select(i => (byte)i)).ToArray(); // 32 bayt
        using var doc = new HexDocument(new MemoryStream(bytes));

        Assert.Equal(2, doc.Count);
        var first = doc[0];
        Assert.Equal("00000000", first.OffsetText);
        Assert.Equal("4D 5A 00 01 02 03 04 05  06 07 08 09 0A 0B 0C 0D", first.HexText);
        Assert.Equal("MZ..............", first.AsciiText);
        Assert.Equal("00000010", doc[1].OffsetText);
    }

    [Fact]
    public void PartialLastRow_AndLargeFilesUseChunks()
    {
        var bytes = new byte[200_003];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i * 7);
        using var doc = new HexDocument(new MemoryStream(bytes));

        Assert.Equal(12_501, doc.Count);
        var last = doc[doc.Count - 1];
        Assert.Equal(200_000, last.Offset);
        Assert.Equal(3, last.AsciiText.Length);

        // Parça sınırını (64 KB) aşan satır doğru birleştirilmeli.
        var boundary = doc.GetRow(65_536 / 16 - 1);
        Assert.Equal(Convert.ToHexString(bytes.AsSpan(65_520, 8)), boundary.HexText[..23].Replace(" ", ""));
        Assert.Equal(0, ((System.Collections.IList)doc).IndexOf(doc[0]));
    }
}

public class PreviewCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SecondOpen_ComesFromCache()
    {
        using var temp = new TempDir();
        temp.WriteFile("kaynak/çiçek.txt", "Merhaba dünya");
        temp.WriteFile("kaynak/büyük.bin", ManagedEngineTests.RandomBytes(300_000));
        var zip = temp["test.zip"];
        await new ManagedEngine().CreateAsync(new CreateRequest(zip, [temp["kaynak"]], CompressionPreset.ForPath(zip)), ct: Ct);
        await using var reader = await new ManagedEngine().OpenAsync(zip, OpenOptions.Default, Ct);
        using var cache = new PreviewCache(memoryEntryLimit: 100_000, directory: temp["önbellek"]);

        var text = reader.Entries.Single(e => e.Name == "çiçek.txt");
        using (var first = await cache.OpenAsync(reader, text, ct: Ct))
            Assert.False(first.FromCache);
        using (var second = await cache.OpenAsync(reader, text, ct: Ct))
        {
            Assert.True(second.FromCache);
            using var stream = second.OpenRead();
            Assert.Equal("Merhaba dünya", await new StreamReader(stream).ReadToEndAsync(Ct));
        }

        // Bellek sınırını aşan girdi geçici dosyaya yazılır; iki bağımsız okuyucu aynı anda açabilir.
        var big = await cache.OpenAsync(reader, reader.Entries.Single(e => e.Name == "büyük.bin"), ct: Ct);
        using (var a = big.OpenRead())
        using (var b = big.OpenRead())
        {
            b.Position = 1000;
            Assert.Equal(300_000, a.Length);
            Assert.Equal(1000, b.Position);
        }
        Assert.Single(Directory.GetFiles(temp["önbellek"]));

        // Gösterilen girdinin dosyası okuyucu kapanınca hemen değil, önizleme bırakınca silinir.
        cache.Evict(reader);
        Assert.Equal(0, cache.Count);
        Assert.Single(Directory.GetFiles(temp["önbellek"]));
        big.Dispose();
        Assert.Empty(Directory.GetFiles(temp["önbellek"]));
    }

    [Fact]
    public async Task Budget_EvictsLeastRecentlyUsed()
    {
        var reader = new FakeReader(solid: false, sizes: [400, 400, 400]);
        using var cache = new PreviewCache(budget: 1000, memoryEntryLimit: 1000, blockPrefetchLimit: 1000);

        (await cache.OpenAsync(reader, reader.Entries[0], ct: Ct)).Dispose();
        (await cache.OpenAsync(reader, reader.Entries[1], ct: Ct)).Dispose();
        (await cache.OpenAsync(reader, reader.Entries[0], ct: Ct)).Dispose(); // 0 artık en son kullanılan
        (await cache.OpenAsync(reader, reader.Entries[2], ct: Ct)).Dispose(); // bütçe aşılır: 1 atılır

        Assert.True(cache.Contains(reader, 0));
        Assert.False(cache.Contains(reader, 1));
        Assert.True(cache.Contains(reader, 2));
        Assert.Equal(800, cache.Size);
    }

    [Fact]
    public async Task ShownEntry_IsNotEvicted()
    {
        var reader = new FakeReader(solid: false, sizes: [600, 600]);
        using var cache = new PreviewCache(budget: 1000, memoryEntryLimit: 1000, blockPrefetchLimit: 1000);

        using var shown = await cache.OpenAsync(reader, reader.Entries[0], ct: Ct);
        (await cache.OpenAsync(reader, reader.Entries[1], ct: Ct)).Dispose(); // bütçe aşılır ama 0 ekranda

        Assert.True(cache.Contains(reader, 0));
        using var stream = shown.OpenRead();
        Assert.Equal(600, stream.Length);
    }

    [Fact]
    public async Task SolidBlock_IsDecodedOnceForSequentialBrowsing()
    {
        // İki blok: 0-2 ve 3-4.
        var reader = new FakeReader(solid: true, sizes: [100, 100, 100, 100, 100], blocks: [0, 0, 0, 1, 1]);
        using var cache = new PreviewCache(budget: 1 << 20, memoryEntryLimit: 1 << 20, blockPrefetchLimit: 1 << 20);

        using (var first = await cache.OpenAsync(reader, reader.Entries[1], ct: Ct))
            Assert.False(first.FromCache);
        Assert.Equal([[0, 1, 2]], reader.BatchCalls);

        using (var zero = await cache.OpenAsync(reader, reader.Entries[0], ct: Ct))
            Assert.True(zero.FromCache);
        using (var two = await cache.OpenAsync(reader, reader.Entries[2], ct: Ct))
            Assert.True(two.FromCache);
        Assert.Single(reader.BatchCalls);

        (await cache.OpenAsync(reader, reader.Entries[3], ct: Ct)).Dispose();
        Assert.Equal([3, 4], reader.BatchCalls[1]);
        Assert.Equal(0, reader.SingleCalls);
    }

    [Fact]
    public void SolidGroup_RespectsPrefetchLimit()
    {
        var reader = new FakeReader(solid: true, sizes: [300, 300, 300, 300], blocks: [0, 0, 0, 0]);
        using var cache = new PreviewCache(budget: 10_000, blockPrefetchLimit: 700, readAheadLimit: 300);

        // Hedef 1: önceki girdi (0) zaten çözülecek; sonrakilerden yalnızca ileri okuma payı kadar.
        Assert.Equal([0, 1, 2], cache.SolidGroup(reader, reader.Entries[1]).Select(e => e.Index));

        // Solid değilse veya okuyucu tek geçişte okuyamıyorsa (yönetilen tar.gz) yalnızca hedef.
        var plain = new FakeReader(solid: false, sizes: [10, 10]);
        Assert.Equal([1], cache.SolidGroup(plain, plain.Entries[1]).Select(e => e.Index));
        var sequential = new FakeReader(solid: true, sizes: [10, 10]) { BatchRead = false };
        Assert.Equal([1], cache.SolidGroup(sequential, sequential.Entries[1]).Select(e => e.Index));

        // 7z'de boş dosyanın bloğu yok: bütün arşivi açmaya kalkışma.
        var withEmpty = new FakeReader(solid: true, sizes: [100, 0, 100], blocks: [0, null, 0]);
        Assert.Equal([1], cache.SolidGroup(withEmpty, withEmpty.Entries[1]).Select(e => e.Index));
    }

    [Fact]
    public async Task FailedBlock_FallsBackToSingleEntryForRealError()
    {
        var reader = new FakeReader(solid: true, sizes: [10, 10], blocks: [0, 0]) { FailBatch = true, PasswordRequired = true };
        using var cache = new PreviewCache();

        await Assert.ThrowsAsync<ArchivePasswordException>(() => cache.OpenAsync(reader, reader.Entries[1], ct: Ct));
        Assert.Equal(1, reader.SingleCalls);
    }

    [Fact]
    public async Task Dispose_RemovesTempDirectory()
    {
        using var temp = new TempDir();
        var reader = new FakeReader(solid: false, sizes: [5000]);
        var cache = new PreviewCache(memoryEntryLimit: 100, directory: temp["önbellek"]);
        (await cache.OpenAsync(reader, reader.Entries[0], ct: Ct)).Dispose();
        Assert.True(Directory.Exists(temp["önbellek"]));

        cache.Dispose();
        Assert.False(Directory.Exists(temp["önbellek"]));
    }

    /// <summary>Bellekteki sahte okuyucu: girdi i'nin içeriği (byte)i ile dolu.</summary>
    private sealed class FakeReader : IArchiveReader
    {
        public FakeReader(bool solid, int[] sizes, int?[]? blocks = null)
        {
            IsSolid = solid;
            Entries = [.. sizes.Select((size, i) => new ArchiveEntry(i, $"dosya{i}.bin", false, size, Block: blocks?[i]))];
        }

        public List<int[]> BatchCalls { get; } = [];
        public int SingleCalls { get; private set; }
        public bool FailBatch { get; init; }
        public bool BatchRead { get; init; } = true;
        public bool SupportsBatchRead => BatchRead;
        public bool PasswordRequired { get; init; }

        public string ArchivePath => "sahte.7z";
        public ArchiveFormat Format => ArchiveFormat.SevenZip;
        public string FormatName => "7z";
        public string EngineName => "sahte";
        public IReadOnlyList<ArchiveEntry> Entries { get; }
        public bool IsSolid { get; }

        public Task<Stream> OpenEntryAsync(int index, CancellationToken ct = default)
        {
            SingleCalls++;
            if (PasswordRequired)
                throw new ArchivePasswordException(ArchivePath, wasProvided: false);
            return Task.FromResult<Stream>(new MemoryStream(Content(index)));
        }

        public Task ReadEntriesAsync(IReadOnlyCollection<int> indices, IEntryReceiver receiver,
            IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
        {
            BatchCalls.Add([.. indices.Order()]);
            if (FailBatch)
                throw new IOException("blok açılamadı");
            foreach (var index in indices.Order())
            {
                if (receiver.Begin(Entries[index]) is { } target)
                {
                    target.Write(Content(index));
                    receiver.End(Entries[index], null);
                }
            }
            return Task.CompletedTask;
        }

        private byte[] Content(int index) => Enumerable.Repeat((byte)index, (int)Entries[index].Size).ToArray();

        public Task<ExtractionResult> ExtractAsync(IReadOnlyCollection<int>? indices, ExtractionOptions options,
            IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AfuZip.Core.TestResult> TestAsync(IReadOnlyCollection<int>? indices = null, IProgress<ArchiveProgress>? progress = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

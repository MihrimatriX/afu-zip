using System.Buffers.Binary;
using System.IO.Compression;
using AfuZip.Core;

namespace AfuZip.Engine.Managed;

/// <summary>Tek dosyalık .gz: içinde tek girdi vardır, adı arşiv adından türetilir.</summary>
internal sealed class GZipFileReader : ManagedReaderBase
{
    private GZipFileReader(string path, IReadOnlyList<ArchiveEntry> entries)
        : base(path, ArchiveFormat.GZip, "gzip", entries)
    {
    }

    public static GZipFileReader Open(string path)
    {
        var info = new FileInfo(path);
        var name = Path.GetFileName(path);
        name = name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name + ".out";

        var entry = new ArchiveEntry(0, name, false, ReadOriginalSize(path), info.Length, info.LastWriteTimeUtc, Method: "Deflate");
        return new GZipFileReader(path, [entry]);
    }

    protected override async Task CopyEntryAsync(int index, Stream destination, CancellationToken ct)
    {
        await using var source = OpenSource();
        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    protected override async Task<ExtractionResult> ExtractCoreAsync(IReadOnlyList<ArchiveEntry> selected, ExtractionSession session,
        IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        foreach (var entry in selected)
        {
            await WriteFileAsync(session, entry, OpenSource, ct).ConfigureAwait(false);
            progress?.Report(new(entry.Size, entry.Size, entry.Path));
        }
        return session.Result;
    }

    private Stream OpenSource() => new GZipStream(File.OpenRead(ArchivePath), CompressionMode.Decompress);

    /// <summary>GZip'in son 4 baytı orijinal boyutun 2^32 modudur; 4 GB üstü dosyalarda yaklaşık değerdir.</summary>
    private static long ReadOriginalSize(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length < 18)
            return 0;
        Span<byte> tail = stackalloc byte[4];
        file.Seek(-4, SeekOrigin.End);
        file.ReadExactly(tail);
        return BinaryPrimitives.ReadUInt32LittleEndian(tail);
    }
}

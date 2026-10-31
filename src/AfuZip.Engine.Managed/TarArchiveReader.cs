using System.Formats.Tar;
using System.IO.Compression;
using AfuZip.Core;

namespace AfuZip.Engine.Managed;

/// <summary>
/// TAR ve tar.gz okuyucu. TAR yalnızca ileri doğru okunabildiği için her işlemde stream baştan taranır;
/// girdinin tar içindeki sırası <see cref="_ordinals"/>'da tutulur.
/// </summary>
internal sealed class TarArchiveReader : ManagedReaderBase
{
    private readonly bool _gzip;
    private readonly int[] _ordinals;

    private TarArchiveReader(string path, bool gzip, IReadOnlyList<ArchiveEntry> entries, int[] ordinals)
        : base(path, gzip ? ArchiveFormat.GZip : ArchiveFormat.Tar, gzip ? "tar.gz" : "tar", entries)
    {
        _gzip = gzip;
        _ordinals = ordinals;
    }

    // tar.gz bir bütün olarak sıkıştırılır: tek dosyaya erişmek öncekileri açmayı gerektirir.
    public override bool IsSolid => _gzip;

    public static TarArchiveReader Open(string path, bool gzip)
    {
        var entries = new List<ArchiveEntry>();
        var ordinals = new List<int>();

        using var stream = OpenTarStream(path, gzip);
        using var reader = new TarReader(stream);
        var ordinal = 0;
        while (reader.GetNextEntry() is { } tarEntry)
        {
            var kind = Classify(tarEntry.EntryType);
            if (kind is not null)
            {
                entries.Add(new ArchiveEntry(
                    Index: entries.Count,
                    Path: ArchiveEntry.NormalizePath(tarEntry.Name),
                    IsDirectory: kind == EntryKind.Directory,
                    Size: tarEntry.Length,
                    Modified: tarEntry.ModificationTime,
                    Method: gzip ? "Deflate" : "Store",
                    IsSymlink: kind == EntryKind.Link));
                ordinals.Add(ordinal);
            }
            ordinal++;
        }

        return new TarArchiveReader(path, gzip, entries, [.. ordinals]);
    }

    protected override async Task CopyEntryAsync(int index, Stream destination, CancellationToken ct)
    {
        var wanted = _ordinals[index];
        await using var stream = OpenTarStream(ArchivePath, _gzip);
        await using var reader = new TarReader(stream);
        var ordinal = 0;
        while (await reader.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } tarEntry)
        {
            if (ordinal++ != wanted)
                continue;
            if (tarEntry.DataStream is { } data)
                await data.CopyToAsync(destination, ct).ConfigureAwait(false);
            return;
        }
        throw new InvalidDataException("Girdi tar içinde bulunamadı.");
    }

    protected override async Task<ExtractionResult> ExtractCoreAsync(IReadOnlyList<ArchiveEntry> selected, ExtractionSession session,
        IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var wanted = selected.ToDictionary(e => _ordinals[e.Index]);
        var total = selected.Sum(e => e.Size);
        long done = 0;

        await using var stream = OpenTarStream(ArchivePath, _gzip);
        await using var reader = new TarReader(stream);
        var ordinal = 0;
        while (wanted.Count > 0 && await reader.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } tarEntry)
        {
            if (!wanted.Remove(ordinal++, out var entry))
                continue;

            if (entry.IsDirectory)
            {
                session.CreateDirectory(entry);
            }
            else if (entry.IsSymlink)
            {
                // BeginFile, politikaya göre engeller ve sonuca işler.
                session.BeginFile(entry);
            }
            else
            {
                var data = tarEntry.DataStream ?? Stream.Null;
                await WriteFileAsync(session, entry, () => new NonDisposingStream(data), ct).ConfigureAwait(false);
            }

            done += entry.Size;
            progress?.Report(new(done, total, entry.Path));
        }

        return session.Result;
    }

    // Tek geçiş: varsayılan uygulama her girdi için stream'i baştan tarardı (O(n²)).
    protected override async Task<TestResult> TestCoreAsync(IReadOnlyList<ArchiveEntry> selected,
        IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var result = new TestResult();
        var wanted = selected.Where(e => !e.IsDirectory && !e.IsSymlink).ToDictionary(e => _ordinals[e.Index]);
        var total = wanted.Values.Sum(e => e.Size);
        long done = 0;

        await using var stream = OpenTarStream(ArchivePath, _gzip);
        await using var reader = new TarReader(stream);
        var ordinal = 0;
        try
        {
            while (wanted.Count > 0 && await reader.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } tarEntry)
            {
                if (!wanted.Remove(ordinal++, out var entry))
                    continue;

                var data = tarEntry.DataStream ?? Stream.Null;
                await TestEntryAsync(entry, target => data.CopyToAsync(target, ct), result).ConfigureAwait(false);
                done += entry.Size;
                progress?.Report(new(done, total, entry.Path));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            // Bozuk/yarım tar: kalan girdilere ulaşılamaz.
            foreach (var missing in wanted.Values)
                result.AddFailed(missing.Path, $"okunamadı: {ex.Message}");
            wanted.Clear();
        }

        foreach (var missing in wanted.Values)
            result.AddFailed(missing.Path, "beklenmeyen dosya sonu");
        return result;
    }

    private static Stream OpenTarStream(string path, bool gzip)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return gzip ? new GZipStream(file, CompressionMode.Decompress) : file;
    }

    private enum EntryKind { File, Directory, Link }

    private static EntryKind? Classify(TarEntryType type) => type switch
    {
        TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => EntryKind.File,
        TarEntryType.Directory => EntryKind.Directory,
        TarEntryType.SymbolicLink or TarEntryType.HardLink => EntryKind.Link,
        _ => null, // aygıt, FIFO, pax global başlıkları listelenmez
    };

    /// <summary>TarReader veri stream'ini kendisi yönetir; WriteFileAsync onu kapatmasın.</summary>
    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

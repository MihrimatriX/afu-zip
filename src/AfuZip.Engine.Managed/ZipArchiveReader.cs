using System.IO.Compression;
using AfuZip.Core;

namespace AfuZip.Engine.Managed;

internal sealed class ZipArchiveReader : ManagedReaderBase
{
    private readonly ZipArchive _zip;

    private ZipArchiveReader(string path, ZipArchive zip, IReadOnlyList<ArchiveEntry> entries)
        : base(path, ArchiveFormat.Zip, "zip", entries) => _zip = zip;

    public static ZipArchiveReader Open(string path)
    {
        var zip = ZipFile.OpenRead(path);
        try
        {
            var entries = zip.Entries.Select((e, i) =>
            {
                var name = ArchiveEntry.NormalizePath(e.FullName);
                var isDir = name.EndsWith('/');
                return new ArchiveEntry(
                    Index: i,
                    Path: name,
                    IsDirectory: isDir,
                    Size: e.Length,
                    PackedSize: e.CompressedLength,
                    Modified: e.LastWriteTime,
                    Crc: isDir ? null : e.Crc32,
                    Method: isDir ? null : e.CompressedLength == e.Length ? "Store" : "Deflate",
                    IsEncrypted: e.IsEncrypted);
            }).ToList();
            return new ZipArchiveReader(path, zip, entries);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    protected override async Task CopyEntryAsync(int index, Stream destination, CancellationToken ct)
    {
        await using var source = OpenSource(index);
        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    protected override async Task<ExtractionResult> ExtractCoreAsync(IReadOnlyList<ArchiveEntry> selected, ExtractionSession session,
        IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var total = selected.Sum(e => e.Size);
        long done = 0;

        foreach (var entry in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
            {
                session.CreateDirectory(entry);
                continue;
            }

            if (entry.IsEncrypted)
            {
                session.FailFile(entry, null, "şifreli ZIP girdileri yönetilen motorla açılamaz; 7-Zip motoru gerekir");
                continue;
            }

            await WriteFileAsync(session, entry, () => OpenSource(entry.Index), ct).ConfigureAwait(false);
            done += entry.Size;
            progress?.Report(new(done, total, entry.Path));
        }

        return session.Result;
    }

    private Stream OpenSource(int index) => _zip.Entries[index].Open();

    public override async ValueTask DisposeAsync()
    {
        _zip.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

using System.Formats.Tar;
using System.IO.Compression;
using AfuZip.Core;

namespace AfuZip.Engine.Managed;

/// <summary>
/// Native bağımlılığı olmayan yedek motor: ZIP, TAR, tar.gz ve tek dosyalık GZip.
/// 7z.dll bulunamadığında ve testlerde kullanılır.
/// </summary>
public sealed class ManagedEngine : IArchiveEngine
{
    public string Name => "managed";

    public bool IsAvailable => true;

    public bool Supports(ArchiveFormat format, Capability needed) => needed switch
    {
        Capability.Read or Capability.Write => format is ArchiveFormat.Zip or ArchiveFormat.Tar or ArchiveFormat.GZip,
        _ => false,
    };

    public Task<IArchiveReader> OpenAsync(string archivePath, OpenOptions options, CancellationToken ct = default) =>
        Task.Run<IArchiveReader>(() =>
        {
            var format = options.FormatHint != ArchiveFormat.Unknown ? options.FormatHint : FormatSniffer.Detect(archivePath);
            return format switch
            {
                ArchiveFormat.Zip => ZipArchiveReader.Open(archivePath),
                ArchiveFormat.Tar => TarArchiveReader.Open(archivePath, gzip: false),
                ArchiveFormat.GZip when GZipContainsTar(archivePath) => TarArchiveReader.Open(archivePath, gzip: true),
                ArchiveFormat.GZip => GZipFileReader.Open(archivePath),
                _ => throw new NotSupportedException($"Yönetilen motor {format.DisplayName()} formatını açamaz."),
            };
        }, ct);

    public Task CreateAsync(CreateRequest request, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            if (request.Password is not null)
                throw new NotSupportedException("Yönetilen motor şifreli arşiv oluşturamaz; 7-Zip motoru gerekir.");

            var preset = request.Preset;
            var items = SourceCollector.Collect(request.Sources);
            var total = items.Sum(i => i.Size);
            var temp = request.ArchivePath + ".afuzip-tmp";

            try
            {
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    switch (preset.Format)
                    {
                        case ArchiveFormat.Zip:
                            await WriteZipAsync(output, items, ToZipLevel(preset.Level), total, progress, ct);
                            break;
                        case ArchiveFormat.Tar:
                            await WriteTarAsync(output, items, total, progress, ct);
                            break;
                        case ArchiveFormat.GZip when preset.TarWrapped:
                            await using (var gzip = new GZipStream(output, ToZipLevel(preset.Level), leaveOpen: true))
                                await WriteTarAsync(gzip, items, total, progress, ct);
                            break;
                        default:
                            throw new NotSupportedException($"Yönetilen motor '{preset.DisplayName}' presetiyle arşiv oluşturamaz.");
                    }
                }

                File.Move(temp, request.ArchivePath, overwrite: true);
            }
            catch
            {
                File.Delete(temp);
                throw;
            }
        }, ct);

    private static async Task WriteZipAsync(Stream output, IReadOnlyList<SourceItem> items, CompressionLevel level,
        long total, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        await using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        long done = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.IsDirectory)
            {
                zip.CreateEntry(item.EntryName.TrimEnd('/') + "/");
                continue;
            }

            var entry = zip.CreateEntry(item.EntryName, level);
            entry.LastWriteTime = File.GetLastWriteTime(item.FullPath);
            await using (var target = await entry.OpenAsync(ct))
            await using (var source = File.OpenRead(item.FullPath))
                await source.CopyToAsync(target, ct);

            done += item.Size;
            progress?.Report(new(done, total, item.EntryName));
        }
    }

    private static async Task WriteTarAsync(Stream output, IReadOnlyList<SourceItem> items,
        long total, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        await using var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        long done = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            await tar.WriteEntryAsync(item.FullPath, item.EntryName, ct);
            done += item.Size;
            progress?.Report(new(done, total, item.EntryName));
        }
    }

    private static CompressionLevel ToZipLevel(int level) => level switch
    {
        <= 0 => CompressionLevel.NoCompression,
        <= 3 => CompressionLevel.Fastest,
        <= 6 => CompressionLevel.Optimal,
        _ => CompressionLevel.SmallestSize,
    };

    private static bool GZipContainsTar(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var header = new byte[512];
            var read = gzip.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return FormatSniffer.LooksLikeTar(header.AsSpan(0, read));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}

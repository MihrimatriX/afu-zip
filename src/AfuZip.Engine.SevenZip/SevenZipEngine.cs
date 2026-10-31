using AfuZip.Core;

namespace AfuZip.Engine.SevenZip;

/// <summary>
/// 7z.dll tabanlı ana motor: ~50 biçimi okur; 7z, zip, tar, gzip, bzip2, xz, wim (7-Zip ZS ile zstd) yazar ve günceller.
/// </summary>
public sealed class SevenZipEngine(string? libraryPath = null) : IArchiveEngine
{
    private readonly Lazy<SevenZipLibrary?> _library = new(() => SevenZipLibrary.TryLoad(libraryPath));

    public string Name => "7-zip";

    public bool IsAvailable => _library.Value is not null;

    /// <summary>Yüklenen 7z.dll'in yolu; bulunamadıysa null.</summary>
    public string? LibraryPath => _library.Value?.Path;

    public IReadOnlyList<SevenZipFormat> Formats => _library.Value?.Formats ?? [];

    /// <summary>Yüklü 7z.dll Zstandard biliyor mu (7-Zip ZS). Ana 7-Zip'te yok; ZSTD presetleri LZMA2'ye düşer.</summary>
    public bool HasZstd => _library.Value?.FindFormat("zstd") is not null;

    // 7-Zip bilinmeyen formatları da imza ve uzantıyla dener; bu yüzden Unknown dahil her formatı okur.
    public bool Supports(ArchiveFormat format, Capability needed)
    {
        if (_library.Value is not { } library)
            return false;
        if (needed == Capability.Read)
            return true;
        if (SevenZipLibrary.HandlerName(format) is not { } handler || library.FindFormat(handler) is not { CanUpdate: true })
            return false;
        return !needed.HasFlag(Capability.Encrypt) || format is ArchiveFormat.SevenZip or ArchiveFormat.Zip;
    }

    public Task<IArchiveReader> OpenAsync(string archivePath, OpenOptions options, CancellationToken ct = default)
    {
        var library = _library.Value ?? throw new NotSupportedException("7z.dll bulunamadı.");
        return Task.Run<IArchiveReader>(() => SevenZipReader.Open(library, archivePath, options, ct), ct);
    }

    public Task CreateAsync(CreateRequest request, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        var library = _library.Value ?? throw new NotSupportedException("7z.dll bulunamadı.");
        return Task.Run(() => SevenZipWriter.Create(library, request, progress, ct), ct);
    }

    public Task<PendingArchive> PrepareUpdateAsync(string archivePath, ArchiveUpdate update, OpenOptions options,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        var library = _library.Value ?? throw new NotSupportedException("7z.dll bulunamadı.");
        return Task.Run(() => new PendingArchive(SevenZipWriter.PrepareUpdate(library, archivePath, update, options, progress, ct)), ct);
    }
}

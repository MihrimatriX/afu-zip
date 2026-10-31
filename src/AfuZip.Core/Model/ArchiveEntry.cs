namespace AfuZip.Core;

/// <summary>Arşivdeki bir girdi. <see cref="Path"/> her zaman '/' ayraçlı, arşivdeki haliyle tutulur.</summary>
public sealed record ArchiveEntry(
    int Index,
    string Path,
    bool IsDirectory,
    long Size,
    long? PackedSize = null,
    DateTimeOffset? Modified = null,
    uint? Crc = null,
    string? Method = null,
    bool IsEncrypted = false,
    bool IsSymlink = false,
    int? Block = null)
{
    // Block: solid arşivde girdinin ait olduğu sıkıştırma bloğu (7z "folder"). Aynı bloktaki bir dosyayı açmak,
    // bloğun o dosyadan önceki kısmını da çözmeyi gerektirir; önizleme önbelleği bloğu topluca açar.

    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            return slash < 0 ? trimmed : trimmed[(slash + 1)..];
        }
    }

    /// <summary>Sıkıştırılmış / orijinal boyut. 0.25 = %75 kazanç.</summary>
    public double? Ratio => PackedSize is { } packed && Size > 0 ? (double)packed / Size : null;

    public static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
}

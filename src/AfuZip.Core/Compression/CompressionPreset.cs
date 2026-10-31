namespace AfuZip.Core;

/// <summary>Kullanıcı codec yerine niyet seçer; preset bunu format ve ayarlara çevirir.</summary>
/// <param name="TarWrapped">true ise önce tar, sonra <paramref name="Format"/> ile sıkıştırılır (tar.zst gibi).</param>
public sealed record CompressionPreset(
    string Id,
    string DisplayName,
    ArchiveFormat Format,
    string Method,
    int Level,
    bool Solid,
    string Description,
    bool TarWrapped = false)
{
    public static CompressionPreset Fast { get; } =
        new("fast", "Hızlı", ArchiveFormat.SevenZip, "ZSTD", 3, false, "Büyük dosyalar, günlük yedek");

    public static CompressionPreset Balanced { get; } =
        new("balanced", "Dengeli", ArchiveFormat.SevenZip, "LZMA2", 5, true, "Genel kullanım");

    public static CompressionPreset Maximum { get; } =
        new("max", "Maksimum", ArchiveFormat.SevenZip, "LZMA2", 9, true, "Arşivleme ve dağıtım; exe'lerde BCJ2/ARM64 filtresi");

    public static CompressionPreset Share { get; } =
        new("share", "Paylaşım", ArchiveFormat.Zip, "Deflate", 6, false, "Karşı tarafın ne kullandığı bilinmiyorsa");

    public static CompressionPreset Developer { get; } =
        new("dev", "Geliştirici", ArchiveFormat.Zstd, "ZSTD", 19, true, "Linux, CI ve paket dosyaları (tar.zst)", TarWrapped: true);

    public static IReadOnlyList<CompressionPreset> All { get; } = [Fast, Balanced, Maximum, Share, Developer];

    /// <summary>Hedef dosya adından preset çıkarır: .zip → Paylaşım, .tar.gz → tar + gzip, .7z → Dengeli.</summary>
    public static CompressionPreset ForPath(string archivePath)
    {
        var name = Path.GetFileName(archivePath).ToLowerInvariant();
        if (name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tgz", StringComparison.Ordinal))
            return new("tar-gz", "tar.gz", ArchiveFormat.GZip, "Deflate", 6, true, "tar + gzip", TarWrapped: true);
        if (name.EndsWith(".tar.zst", StringComparison.Ordinal) || name.EndsWith(".tzst", StringComparison.Ordinal))
            return Developer;
        if (name.EndsWith(".tar.xz", StringComparison.Ordinal) || name.EndsWith(".txz", StringComparison.Ordinal))
            return new("tar-xz", "tar.xz", ArchiveFormat.Xz, "LZMA2", 6, true, "tar + xz", TarWrapped: true);
        if (name.EndsWith(".tar.bz2", StringComparison.Ordinal) || name.EndsWith(".tbz2", StringComparison.Ordinal))
            return new("tar-bz2", "tar.bz2", ArchiveFormat.BZip2, "BZip2", 9, true, "tar + bzip2", TarWrapped: true);

        return FormatSniffer.FromExtension(archivePath) switch
        {
            ArchiveFormat.Zip => Share,
            ArchiveFormat.Tar => new("tar", "TAR", ArchiveFormat.Tar, "Store", 0, false, "Sıkıştırmasız tar"),
            ArchiveFormat.SevenZip => Balanced,
            // Tek dosya sıkıştırıcıları: yalnızca bir dosya alır (klasör için tar.* kullanılır).
            ArchiveFormat.GZip => new("gz", "gzip", ArchiveFormat.GZip, "Deflate", 6, false, "Tek dosya, gzip"),
            ArchiveFormat.Xz => new("xz", "xz", ArchiveFormat.Xz, "LZMA2", 6, false, "Tek dosya, xz"),
            ArchiveFormat.BZip2 => new("bz2", "bzip2", ArchiveFormat.BZip2, "BZip2", 9, false, "Tek dosya, bzip2"),
            ArchiveFormat.Zstd => new("zst", "zstd", ArchiveFormat.Zstd, "ZSTD", 9, false, "Tek dosya, Zstandard"),
            ArchiveFormat.Wim => new("wim", "WIM", ArchiveFormat.Wim, "Copy", 0, false, "Windows görüntüsü"),
            var other => throw new NotSupportedException($"'{Path.GetExtension(archivePath)}' uzantısı için preset yok ({other.DisplayName()})."),
        };
    }
}

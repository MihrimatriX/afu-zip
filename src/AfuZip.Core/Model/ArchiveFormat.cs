namespace AfuZip.Core;

/// <summary>Konteyner veya stream formatı. Codec (LZMA2, Deflate...) ayrı bir kavramdır.</summary>
public enum ArchiveFormat
{
    Unknown = 0,
    SevenZip,
    Zip,
    Rar4,
    Rar5,
    Tar,
    GZip,
    BZip2,
    Xz,
    Zstd,
    Lz4,
    Brotli,
    Lzma,
    Iso,
    Cab,
    Wim,
}

public static class ArchiveFormatExtensions
{
    public static string DisplayName(this ArchiveFormat format) => format switch
    {
        ArchiveFormat.SevenZip => "7z",
        ArchiveFormat.Zip => "ZIP",
        ArchiveFormat.Rar4 => "RAR4",
        ArchiveFormat.Rar5 => "RAR5",
        ArchiveFormat.Tar => "TAR",
        ArchiveFormat.GZip => "GZip",
        ArchiveFormat.BZip2 => "BZip2",
        ArchiveFormat.Xz => "XZ",
        ArchiveFormat.Zstd => "Zstandard",
        ArchiveFormat.Lz4 => "LZ4",
        ArchiveFormat.Brotli => "Brotli",
        ArchiveFormat.Lzma => "LZMA",
        ArchiveFormat.Iso => "ISO",
        ArchiveFormat.Cab => "CAB",
        ArchiveFormat.Wim => "WIM",
        _ => "Bilinmiyor",
    };

    /// <summary>Tek bir dosyayı sıkıştıran, dosya listesi olmayan formatlar (genelde tar ile birlikte kullanılır).</summary>
    public static bool IsSingleStream(this ArchiveFormat format) => format is
        ArchiveFormat.GZip or ArchiveFormat.BZip2 or ArchiveFormat.Xz or ArchiveFormat.Zstd or
        ArchiveFormat.Lz4 or ArchiveFormat.Brotli or ArchiveFormat.Lzma;
}

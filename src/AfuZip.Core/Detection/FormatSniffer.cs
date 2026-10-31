namespace AfuZip.Core;

/// <summary>
/// Formatı önce içerik imzasından (magic bytes), bulamazsa uzantıdan tespit eder.
/// Uzantıya güvenilmez: "fatura.pdf.zip" bir RAR olabilir.
/// </summary>
public static class FormatSniffer
{
    /// <summary>ISO imzası 0x8001 ofsetinde olduğu için başlıktan bu kadar okunur.</summary>
    public const int HeaderLength = 0x8001 + 5;

    private static readonly (int Offset, byte[] Magic, ArchiveFormat Format)[] Signatures =
    [
        (0, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], ArchiveFormat.SevenZip),
        (0, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00], ArchiveFormat.Rar5),
        (0, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00], ArchiveFormat.Rar4),
        (0, [0x50, 0x4B, 0x03, 0x04], ArchiveFormat.Zip),
        (0, [0x50, 0x4B, 0x05, 0x06], ArchiveFormat.Zip),              // boş ZIP
        (0, [0x50, 0x4B, 0x07, 0x08], ArchiveFormat.Zip),              // çok parçalı ZIP
        (0, [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00], ArchiveFormat.Xz),
        (0, [0x28, 0xB5, 0x2F, 0xFD], ArchiveFormat.Zstd),
        (0, [0x04, 0x22, 0x4D, 0x18], ArchiveFormat.Lz4),
        (0, [0x1F, 0x8B], ArchiveFormat.GZip),
        (0, [0x42, 0x5A, 0x68], ArchiveFormat.BZip2),                  // "BZh"
        (0, [0x4D, 0x53, 0x43, 0x46], ArchiveFormat.Cab),              // "MSCF"
        (0, [0x4D, 0x53, 0x57, 0x49, 0x4D, 0x00, 0x00, 0x00], ArchiveFormat.Wim), // "MSWIM\0\0\0"
        (257, [0x75, 0x73, 0x74, 0x61, 0x72], ArchiveFormat.Tar),      // "ustar"
        (0x8001, [0x43, 0x44, 0x30, 0x30, 0x31], ArchiveFormat.Iso),   // "CD001"
    ];

    private static readonly Dictionary<string, ArchiveFormat> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".7z"] = ArchiveFormat.SevenZip,
        [".zip"] = ArchiveFormat.Zip, [".zipx"] = ArchiveFormat.Zip, [".jar"] = ArchiveFormat.Zip,
        [".apk"] = ArchiveFormat.Zip, [".docx"] = ArchiveFormat.Zip, [".xlsx"] = ArchiveFormat.Zip,
        [".pptx"] = ArchiveFormat.Zip, [".epub"] = ArchiveFormat.Zip, [".nupkg"] = ArchiveFormat.Zip,
        [".vsix"] = ArchiveFormat.Zip, [".cbz"] = ArchiveFormat.Zip,
        [".rar"] = ArchiveFormat.Rar5, [".cbr"] = ArchiveFormat.Rar5,
        [".tar"] = ArchiveFormat.Tar,
        [".gz"] = ArchiveFormat.GZip, [".tgz"] = ArchiveFormat.GZip,
        [".bz2"] = ArchiveFormat.BZip2, [".tbz2"] = ArchiveFormat.BZip2,
        [".xz"] = ArchiveFormat.Xz, [".txz"] = ArchiveFormat.Xz,
        [".zst"] = ArchiveFormat.Zstd, [".tzst"] = ArchiveFormat.Zstd,
        [".lz4"] = ArchiveFormat.Lz4,
        [".br"] = ArchiveFormat.Brotli,  // Brotli'nin imzası yok
        [".lzma"] = ArchiveFormat.Lzma,  // LZMA-alone'un güvenilir imzası yok
        [".iso"] = ArchiveFormat.Iso,
        [".cab"] = ArchiveFormat.Cab,
        [".wim"] = ArchiveFormat.Wim, [".esd"] = ArchiveFormat.Wim,
    };

    public static ArchiveFormat Sniff(ReadOnlySpan<byte> header)
    {
        foreach (var (offset, magic, format) in Signatures)
        {
            if (header.Length >= offset + magic.Length && header.Slice(offset, magic.Length).SequenceEqual(magic))
                return format;
        }
        return ArchiveFormat.Unknown;
    }

    public static ArchiveFormat FromExtension(string path) =>
        Extensions.TryGetValue(Path.GetExtension(path), out var format) ? format : ArchiveFormat.Unknown;

    public static ArchiveFormat Detect(Stream stream, string? fileName = null)
    {
        var buffer = new byte[HeaderLength];
        var start = stream.CanSeek ? stream.Position : 0;
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (stream.CanSeek)
            stream.Position = start;

        var format = Sniff(buffer.AsSpan(0, read));
        return format != ArchiveFormat.Unknown || fileName is null ? format : FromExtension(fileName);
    }

    public static ArchiveFormat Detect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Detect(stream, path);
    }

    /// <summary>tar.gz gibi katmanlı formatlarda iç stream'in TAR olup olmadığını kontrol eder.</summary>
    public static bool LooksLikeTar(ReadOnlySpan<byte> header) =>
        header.Length >= 262 && header.Slice(257, 5).SequenceEqual("ustar"u8);
}

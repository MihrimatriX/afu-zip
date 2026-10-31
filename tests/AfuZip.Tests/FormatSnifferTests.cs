namespace AfuZip.Tests;

public class FormatSnifferTests
{
    public static TheoryData<byte[], ArchiveFormat> Signatures => new()
    {
        { [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04], ArchiveFormat.SevenZip },
        { [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00], ArchiveFormat.Rar5 },
        { [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0xCF], ArchiveFormat.Rar4 },
        { [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00], ArchiveFormat.Zip },
        { [0x50, 0x4B, 0x05, 0x06, 0x00, 0x00], ArchiveFormat.Zip },
        { [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0x00], ArchiveFormat.Xz },
        { [0x28, 0xB5, 0x2F, 0xFD, 0x24], ArchiveFormat.Zstd },
        { [0x04, 0x22, 0x4D, 0x18, 0x64], ArchiveFormat.Lz4 },
        { [0x1F, 0x8B, 0x08, 0x00], ArchiveFormat.GZip },
        { [0x42, 0x5A, 0x68, 0x39], ArchiveFormat.BZip2 },
        { [0x4D, 0x53, 0x43, 0x46, 0x00], ArchiveFormat.Cab },
        { [0x4D, 0x53, 0x57, 0x49, 0x4D, 0x00, 0x00, 0x00], ArchiveFormat.Wim },
    };

    [Theory]
    [MemberData(nameof(Signatures))]
    public void Sniff_RecognizesSignature(byte[] header, ArchiveFormat expected) =>
        Assert.Equal(expected, FormatSniffer.Sniff(header));

    [Fact]
    public void Sniff_FindsTarMagicAtOffset257()
    {
        var header = new byte[512];
        "ustar"u8.CopyTo(header.AsSpan(257));
        Assert.Equal(ArchiveFormat.Tar, FormatSniffer.Sniff(header));
    }

    [Fact]
    public void Sniff_FindsIsoMagicAtOffset0x8001()
    {
        var header = new byte[FormatSniffer.HeaderLength];
        "CD001"u8.CopyTo(header.AsSpan(0x8001));
        Assert.Equal(ArchiveFormat.Iso, FormatSniffer.Sniff(header));
    }

    [Fact]
    public void Sniff_EmptyOrShortInputIsUnknown()
    {
        Assert.Equal(ArchiveFormat.Unknown, FormatSniffer.Sniff([]));
        Assert.Equal(ArchiveFormat.Unknown, FormatSniffer.Sniff([0x1F]));
    }

    [Fact]
    public void Detect_ContentWinsOverExtension()
    {
        using var stream = new MemoryStream([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4]);
        Assert.Equal(ArchiveFormat.SevenZip, FormatSniffer.Detect(stream, "fatura.pdf.zip"));
        Assert.Equal(0, stream.Position);
    }

    [Theory]
    [InlineData("yedek.tar.br", ArchiveFormat.Brotli)]
    [InlineData("Kitap.CBZ", ArchiveFormat.Zip)]
    [InlineData("install.esd", ArchiveFormat.Wim)]
    [InlineData("notlar.txt", ArchiveFormat.Unknown)]
    public void Detect_FallsBackToExtensionWithoutSignature(string fileName, ArchiveFormat expected)
    {
        using var stream = new MemoryStream("düz metin"u8.ToArray());
        Assert.Equal(expected, FormatSniffer.Detect(stream, fileName));
    }
}

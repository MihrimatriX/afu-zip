using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace AfuZip.Core.Preview;

/// <param name="Truncated">Dosya önizleme sınırından büyüktü; yalnızca baş kısmı çözüldü.</param>
public sealed record DecodedText(string Text, Encoding Encoding, bool Truncated, long BytesRead);

/// <summary>
/// Önizleme için metin kodlaması tespiti. Sıra: BOM → geçerli UTF-8 → BOM'suz UTF-16 → sistemin ANSI
/// kod sayfası (Türkçe Windows'ta Windows-1254). Kullanıcı kodlamayı elle de seçebilir.
/// </summary>
public static class TextDecoder
{
    /// <summary>Monaco'ya gönderilecek en fazla bayt. Daha büyük dosyalarda baş kısım gösterilir.</summary>
    public const int DefaultMaxBytes = 5 * 1024 * 1024;

    // Statik alan başlatıcıları metin sırasıyla ve statik kurucudan ÖNCE çalışır; kod sayfası sağlayıcısı
    // aşağıdaki GetEncoding(1254) çağrılarından önce kaydedilmeli.
    private static readonly bool ProviderRegistered = RegisterProvider();

    /// <summary>Sistemin ANSI kod sayfası (tr-TR → 1254, en-US → 1252).</summary>
    public static Encoding SystemAnsi { get; } = Encoding.GetEncoding(
        CultureInfo.CurrentCulture.TextInfo.ANSICodePage is > 0 and var cp and not 65001 ? cp : 1252);

    /// <summary>Kullanıcının elle seçebileceği kodlamalar; Türkçe arşivlerde sık görülenler başta.</summary>
    public static IReadOnlyList<Encoding> Choices { get; } =
    [
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Encoding.GetEncoding(1254), // Windows Türkçe
        Encoding.GetEncoding(28599), // ISO-8859-9 (Latin-5)
        Encoding.GetEncoding(857), // DOS Türkçe (eski ZIP adları, .nfo dosyaları)
        Encoding.GetEncoding(1252), // Batı Avrupa
        Encoding.Unicode, // UTF-16 LE
        Encoding.BigEndianUnicode,
        Encoding.GetEncoding(437), // DOS ABD (ASCII sanat)
    ];

    /// <summary>
    /// İçerik metin mi? NUL baytı (UTF-16 deseni dışında) veya çok sayıda kontrol karakteri varsa ikili kabul edilir.
    /// </summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0)
            return true;
        if (BomEncoding(head, out _) is not null)
            return true;
        if (LooksLikeUtf16(head) is not null)
            return true;

        var sample = head[..Math.Min(head.Length, PreviewClassifier.HeadSize)];
        if (sample.Contains((byte)0))
            return false;

        var control = 0;
        foreach (var b in sample)
        {
            // Sekme, satır sonu, form feed ve ESC (ANSI renk kodları) metinde normaldir.
            if (b < 0x20 && b is not ((byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C or 0x1B))
                control++;
        }
        return control <= sample.Length / 50;
    }

    public static Encoding Detect(ReadOnlySpan<byte> data, bool truncated)
    {
        if (BomEncoding(data, out _) is { } bom)
            return bom;
        if (LooksLikeUtf16(data) is { } utf16)
            return utf16;
        if (IsValidUtf8(data, truncated))
            return Choices[0];
        return SystemAnsi;
    }

    /// <summary>Stream'in başından en fazla <paramref name="maxBytes"/> baytı okuyup çözer.</summary>
    /// <param name="forced">null: otomatik tespit.</param>
    public static async Task<DecodedText> ReadAsync(Stream stream, Encoding? forced = null, int maxBytes = DefaultMaxBytes,
        CancellationToken ct = default)
    {
        // Bir bayt fazla okuyarak dosyanın sınırdan büyük olup olmadığını anlıyoruz.
        var buffer = new byte[(int)Math.Min(maxBytes + 1L, stream.CanSeek ? Math.Max(stream.Length - stream.Position + 1, 1) : maxBytes + 1L)];
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        var truncated = read > maxBytes;
        var data = buffer.AsSpan(0, Math.Min(read, maxBytes));
        return Decode(data, forced, truncated);
    }

    public static DecodedText Decode(ReadOnlySpan<byte> data, Encoding? forced = null, bool truncated = false)
    {
        var encoding = forced ?? Detect(data, truncated);

        // BOM'u metne katmıyoruz.
        if (BomEncoding(data, out var bomLength) is { } bom && bom.CodePage == encoding.CodePage)
            data = data[bomLength..];

        // Kesilmiş dosyada son karakter yarım kalabilir; yarım çok baytlı diziyi at.
        if (truncated)
            data = data[..CompleteLength(data, encoding)];

        return new DecodedText(encoding.GetString(data), encoding, truncated, data.Length);
    }

    private static bool RegisterProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    }

    private static Encoding? BomEncoding(ReadOnlySpan<byte> data, out int length)
    {
        (Encoding? encoding, length) = data switch
        {
            [0xEF, 0xBB, 0xBF, ..] => ((Encoding?)Choices[0], 3),
            [0xFF, 0xFE, 0x00, 0x00, ..] => (Encoding.UTF32, 4),
            [0xFF, 0xFE, ..] => (Encoding.Unicode, 2),
            [0xFE, 0xFF, ..] => (Encoding.BigEndianUnicode, 2),
            _ => (null, 0),
        };
        return encoding;
    }

    /// <summary>BOM'suz UTF-16: ASCII ağırlıklı metinde baytların yarısı (tek veya çift konumlar) sıfırdır.</summary>
    private static Encoding? LooksLikeUtf16(ReadOnlySpan<byte> data)
    {
        var sample = data[..Math.Min(data.Length & ~1, 4096)];
        if (sample.Length < 16)
            return null;

        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i < sample.Length; i += 2)
        {
            if (sample[i] == 0) evenZeros++;
            if (sample[i + 1] == 0) oddZeros++;
        }

        var pairs = sample.Length / 2;
        if (oddZeros > pairs * 0.4 && evenZeros < pairs * 0.05)
            return Encoding.Unicode; // "A\0B\0"
        if (evenZeros > pairs * 0.4 && oddZeros < pairs * 0.05)
            return Encoding.BigEndianUnicode; // "\0A\0B"
        return null;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> data, bool truncated) =>
        Utf8.IsValid(truncated ? data[..CompleteLength(data, Choices[0])] : data);

    /// <summary>Sondaki yarım karakteri dışarıda bırakan uzunluk.</summary>
    private static int CompleteLength(ReadOnlySpan<byte> data, Encoding encoding)
    {
        switch (encoding.CodePage)
        {
            case 65001:
                // Son 3 bayt içinde bir öncü bayt arayıp beklediği uzunluk sığıyor mu bakıyoruz.
                for (var back = 1; back <= Math.Min(4, data.Length); back++)
                {
                    var b = data[^back];
                    if ((b & 0xC0) == 0x80)
                        continue; // devam baytı
                    var needed = b switch { < 0x80 => 1, >= 0xF0 => 4, >= 0xE0 => 3, >= 0xC0 => 2, _ => 1 };
                    return needed > back ? data.Length - back : data.Length;
                }
                return data.Length;
            case 1200 or 1201:
                return data.Length & ~1;
            case 12000 or 12001:
                return data.Length & ~3;
            default:
                return data.Length;
        }
    }
}

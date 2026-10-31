using System.Buffers.Binary;
using System.Text;

namespace AfuZip.Core.Preview;

public enum PreviewKind
{
    /// <summary>Önizlenecek bir şey yok (klasör, boş seçim).</summary>
    None,
    Image,

    /// <summary>Düz metin veya kod; <see cref="PreviewType.Language"/> Monaco dil kimliği.</summary>
    Text,
    Markdown,
    Json,
    Xml,
    Svg,
    Pdf,
    Video,
    Audio,

    /// <summary>İç içe arşiv: önizlemede bilgi kartı, çift tıkta klasör gibi açılır.</summary>
    Archive,

    /// <summary>Tanınmayan içerik: bilgi kartı + hex görünümü.</summary>
    Binary,
}

/// <param name="Language">Metin türleri için Monaco dil kimliği (ör. "csharp"), diğerlerinde null.</param>
public sealed record PreviewType(PreviewKind Kind, string MimeType, string? Language = null)
{
    public bool IsTextual => Kind is PreviewKind.Text or PreviewKind.Markdown or PreviewKind.Json or PreviewKind.Xml or PreviewKind.Svg;

    /// <summary>WebView2 içinde çizilen türler (Monaco, Markdown, ağaç görünümleri, PDF).</summary>
    public bool IsWeb => IsTextual || Kind == PreviewKind.Pdf;

    public static PreviewType Binary { get; } = new(PreviewKind.Binary, "application/octet-stream");
}

/// <summary>
/// Bir girdinin nasıl önizleneceğine karar verir. Önce içerik imzasına bakılır (uzantısı yanlış veya eksik
/// dosyalar için), sonra uzantıya, en son da içeriğin metin gibi görünüp görünmediğine.
/// </summary>
public static class PreviewClassifier
{
    /// <summary>Sınıflandırma için okunması yeterli baş kısım.</summary>
    public const int HeadSize = 8 * 1024;

    // Sıra önemli: statik başlatıcılar metin sırasıyla çalışır ve harita bu iki türü kullanır.
    private static readonly PreviewType Json = new(PreviewKind.Json, "application/json", "json");
    private static readonly PreviewType Xml = new(PreviewKind.Xml, "application/xml", "xml");
    private static readonly Dictionary<string, PreviewType> ByExtension = BuildExtensionMap();

    public static PreviewType Classify(string name, ReadOnlySpan<byte> head)
    {
        var fileName = Path.GetFileName(name.TrimEnd('/'));
        var extension = Path.GetExtension(fileName);

        if (FromSignature(head, extension) is { } bySignature)
            return bySignature;

        if (ByExtension.TryGetValue(extension, out var byExtension))
        {
            // Uzantı metin diyor ama içerik ikili ise (ör. .dat, yanlış adlandırılmış dosya) hex göster.
            if (byExtension.IsTextual && head.Length > 0 && !TextDecoder.LooksLikeText(head))
                return PreviewType.Binary;
            return byExtension;
        }

        if (head.Length == 0)
            return new(PreviewKind.Text, "text/plain", CodeLanguages.ForFileName(fileName));

        return TextDecoder.LooksLikeText(head) ? FromTextContent(fileName, head) : PreviewType.Binary;
    }

    /// <summary>Uzantısı tanınmayan ama metin olan dosyalar: shebang, XML bildirimi, JSON başlangıcı.</summary>
    private static PreviewType FromTextContent(string fileName, ReadOnlySpan<byte> head)
    {
        var language = CodeLanguages.ForFileName(fileName);
        if (language != "plaintext")
            return new(PreviewKind.Text, "text/plain", language);

        var start = SkipBomAndWhitespace(head);
        if (start.StartsWith("#!"u8))
            return new(PreviewKind.Text, "text/plain", CodeLanguages.FromShebang(Encoding.UTF8.GetString(FirstLine(start))));
        if (start.StartsWith("<?xml"u8))
            return Xml;
        if (start.Length > 0 && (start[0] == (byte)'{' || start[0] == (byte)'['))
            return Json;
        return new(PreviewKind.Text, "text/plain", "plaintext");
    }

    private static PreviewType? FromSignature(ReadOnlySpan<byte> h, string extension)
    {
        if (h.Length < 4)
            return null;

        if (h.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']))
            return Image("image/png");
        if (h.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return Image("image/jpeg");
        if (h.StartsWith("GIF8"u8))
            return Image("image/gif");
        if (h.StartsWith("II*\0"u8) || h.StartsWith("MM\0*"u8))
            return Image("image/tiff");
        if (h.StartsWith("%PDF-"u8))
            return new(PreviewKind.Pdf, "application/pdf");
        if (h.StartsWith("fLaC"u8))
            return Audio("audio/flac");
        if (h.StartsWith("ID3"u8))
            return Audio("audio/mpeg");
        if (h.StartsWith("OggS"u8))
            return extension.Equals(".ogv", StringComparison.OrdinalIgnoreCase) ? Video("video/ogg") : Audio("audio/ogg");
        if (h.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]))
            return extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ? Video("video/webm") : Video("video/x-matroska");

        if (h.Length >= 12 && h.StartsWith("RIFF"u8))
        {
            var form = h.Slice(8, 4);
            if (form.SequenceEqual("WEBP"u8))
                return Image("image/webp");
            if (form.SequenceEqual("WAVE"u8))
                return Audio("audio/wav");
            if (form.SequenceEqual("AVI "u8))
                return Video("video/x-msvideo");
        }

        // ISO-BMFF (MP4, MOV, M4A, HEIC, AVIF): 4 bayt kutu boyu + "ftyp" + marka.
        if (h.Length >= 12 && h.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(h.Slice(8, 4));
            return brand switch
            {
                "heic" or "heix" or "hevc" or "hevx" or "mif1" or "msf1" => Image("image/heic"),
                "avif" or "avis" => Image("image/avif"),
                "M4A " or "M4B " or "M4P " => Audio("audio/mp4"),
                "qt  " => Video("video/quicktime"),
                _ => Video("video/mp4"),
            };
        }

        // Çerçeve senkronlu MP3 (ID3 etiketi olmadan): 11 bit 1, MPEG sürümü ve katman geçerli.
        if (h[0] == 0xFF && (h[1] & 0xE0) == 0xE0 && (h[1] & 0x18) != 0x08 && (h[1] & 0x06) != 0 &&
            extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            return Audio("audio/mpeg");

        // MPEG-TS: 188 baytlık paketler, her biri 0x47 ile başlar.
        if (h.Length > 376 && h[0] == 0x47 && h[188] == 0x47 && h[376] == 0x47)
            return Video("video/mp2t");

        if (FormatSniffer.Sniff(h) is not ArchiveFormat.Unknown and var format)
            return new(PreviewKind.Archive, MimeForArchive(format));

        // BMP'nin "BM" imzası zayıf; boyut alanı tutarlıysa ve uzantı da uyuyorsa kabul edilir.
        if (h.Length >= 14 && h.StartsWith("BM"u8) && extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) &&
            BinaryPrimitives.ReadUInt32LittleEndian(h[2..]) > 14)
            return Image("image/bmp");

        return null;
    }

    private static string MimeForArchive(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => "application/zip",
        ArchiveFormat.SevenZip => "application/x-7z-compressed",
        ArchiveFormat.Rar4 or ArchiveFormat.Rar5 => "application/vnd.rar",
        ArchiveFormat.GZip => "application/gzip",
        ArchiveFormat.Tar => "application/x-tar",
        _ => "application/octet-stream",
    };

    private static ReadOnlySpan<byte> SkipBomAndWhitespace(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            data = data[3..];
        var i = 0;
        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;
        return data[i..];
    }

    private static ReadOnlySpan<byte> FirstLine(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOfAny((byte)'\r', (byte)'\n');
        return end < 0 ? data[..Math.Min(data.Length, 200)] : data[..end];
    }

    private static PreviewType Image(string mime) => new(PreviewKind.Image, mime);
    private static PreviewType Audio(string mime) => new(PreviewKind.Audio, mime);
    private static PreviewType Video(string mime) => new(PreviewKind.Video, mime);

    private static Dictionary<string, PreviewType> BuildExtensionMap()
    {
        var map = new Dictionary<string, PreviewType>(StringComparer.OrdinalIgnoreCase);

        void Add(PreviewType type, params string[] extensions)
        {
            foreach (var extension in extensions)
                map[extension] = type;
        }

        Add(Image("image/png"), ".png");
        Add(Image("image/jpeg"), ".jpg", ".jpeg", ".jfif");
        Add(Image("image/gif"), ".gif");
        Add(Image("image/bmp"), ".bmp");
        Add(Image("image/webp"), ".webp");
        Add(Image("image/x-icon"), ".ico", ".cur");
        Add(Image("image/tiff"), ".tif", ".tiff");
        Add(Image("image/heic"), ".heic", ".heif");
        Add(Image("image/avif"), ".avif");
        Add(Image("image/vnd.ms-photo"), ".jxr", ".wdp");

        Add(Video("video/mp4"), ".mp4", ".m4v");
        Add(Video("video/quicktime"), ".mov");
        Add(Video("video/x-matroska"), ".mkv");
        Add(Video("video/webm"), ".webm");
        Add(Video("video/x-msvideo"), ".avi");
        Add(Video("video/x-ms-wmv"), ".wmv");
        // .ts ve .mts TypeScript'e bırakılır; MPEG-TS içerik imzasından (0x47 senkron baytı) tanınır.
        Add(Video("video/mp2t"), ".m2ts", ".m2t");
        Add(Audio("audio/mpeg"), ".mp3");
        Add(Audio("audio/mp4"), ".m4a", ".m4b", ".aac");
        Add(Audio("audio/wav"), ".wav");
        Add(Audio("audio/flac"), ".flac");
        Add(Audio("audio/ogg"), ".ogg", ".oga", ".opus");
        Add(Audio("audio/x-ms-wma"), ".wma");

        Add(new(PreviewKind.Pdf, "application/pdf"), ".pdf");
        Add(new(PreviewKind.Markdown, "text/markdown", "markdown"), ".md", ".markdown", ".mdown", ".mkd");
        Add(Json, ".json", ".jsonc", ".json5", ".geojson", ".webmanifest", ".uproject", ".uplugin", ".asmdef", ".asmref",
            ".babelrc", ".eslintrc", ".prettierrc", ".jsonl" /* satır satır; ağaç ilk değeri gösterir */);
        Add(new(PreviewKind.Svg, "image/svg+xml", "xml"), ".svg");
        Add(Xml, ".xml", ".xsd", ".xsl", ".xslt", ".xaml", ".axaml", ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".props",
            ".targets", ".resx", ".config", ".manifest", ".nuspec", ".plist", ".wxs", ".rss", ".atom", ".kml", ".gpx", ".dae",
            ".appxmanifest", ".storyboard", ".uxml");

        // Kod ve düz metin: dil kimliği uzantıdan.
        foreach (var (extension, language) in CodeLanguages.Extensions)
        {
            if (!map.ContainsKey(extension))
                map[extension] = new(PreviewKind.Text, "text/plain", language);
        }

        return map;
    }
}

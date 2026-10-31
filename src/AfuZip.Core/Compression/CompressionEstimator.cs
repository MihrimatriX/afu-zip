namespace AfuZip.Core;

/// <param name="InputBytes">Sıkıştırılacak toplam boyut.</param>
/// <param name="EstimatedBytes">Tahmini arşiv boyutu.</param>
/// <param name="AlreadyCompressedBytes">Zaten sıkıştırılmış içerik (video, jpg, zip...): neredeyse hiç küçülmez.</param>
public sealed record CompressionEstimate(long InputBytes, long EstimatedBytes, TimeSpan EstimatedTime, int Files, long AlreadyCompressedBytes)
{
    public double Ratio => InputBytes > 0 ? (double)EstimatedBytes / InputBytes : 1;
}

/// <summary>
/// Sıkıştırma diyaloğu için kaba boyut ve süre tahmini: dosyalar uzantıya göre gruplanır (metin, ikili,
/// ham medya, zaten sıkıştırılmış) ve her grup için yöntem/seviyeye göre tipik oran ve hız kullanılır.
/// Amaç doğru sayı değil, doğru karar: "Maksimum 4 dakika sürer ama yalnızca %3 kazandırır" gibi.
/// </summary>
public static class CompressionEstimator
{
    private enum Kind
    {
        Text,
        Binary,
        RawMedia,
        Compressed,
        Other,
    }

    private static readonly Dictionary<string, Kind> Kinds = Build();

    public static CompressionEstimate Estimate(IReadOnlyList<SourceItem> sources, CompressionPreset preset, int? threads = null)
    {
        var cores = Math.Max(1, threads ?? Environment.ProcessorCount);
        long input = 0, output = 0, compressed = 0;
        var files = 0;
        var seconds = 0.0;

        foreach (var source in sources)
        {
            if (source.IsDirectory)
                continue;
            files++;
            input += source.Size;

            var kind = Kinds.GetValueOrDefault(Path.GetExtension(source.EntryName), Kind.Other);
            if (kind == Kind.Compressed)
                compressed += source.Size;

            output += (long)(source.Size * Ratio(kind, preset));
            seconds += source.Size / (Speed(kind, preset, cores) * 1024 * 1024);
        }

        // Solid modda çok sayıda küçük benzer dosya birlikte daha iyi sıkışır; dosya başına başlık yükü de var.
        if (preset.Solid && files > 50)
            output = (long)(output * 0.92);
        output += files * (preset.Format == ArchiveFormat.Zip ? 80L : 24L);

        return new CompressionEstimate(input, Math.Min(output, input + files * 100L), TimeSpan.FromSeconds(seconds), files, compressed);
    }

    private static double Ratio(Kind kind, CompressionPreset preset)
    {
        if (preset.Level == 0 || preset.Method is "Copy" or "Store")
            return 1;

        var strong = preset.Level >= 7;
        return (kind, Family(preset)) switch
        {
            (Kind.Compressed, _) => 0.99,
            (Kind.Text, "lzma") => strong ? 0.15 : 0.18,
            (Kind.Text, "zstd") => preset.Level >= 15 ? 0.17 : 0.25,
            (Kind.Text, _) => 0.28,
            (Kind.Binary, "lzma") => strong ? 0.33 : 0.38, // BCJ filtresiyle
            (Kind.Binary, "zstd") => preset.Level >= 15 ? 0.40 : 0.48,
            (Kind.Binary, _) => 0.50,
            (Kind.RawMedia, "lzma") => 0.55,
            (Kind.RawMedia, _) => 0.65,
            (_, "lzma") => strong ? 0.55 : 0.6,
            _ => 0.68,
        };
    }

    /// <summary>MB/sn, tüm çekirdeklerle. Kabaca 2024 masaüstü işlemcisinde ölçülen değerler.</summary>
    private static double Speed(Kind kind, CompressionPreset preset, int cores)
    {
        if (preset.Level == 0 || preset.Method is "Copy" or "Store")
            return 800;

        var perCore = (Family(preset), preset.Level) switch
        {
            ("zstd", <= 3) => 250.0,
            ("zstd", <= 9) => 60.0,
            ("zstd", <= 15) => 15.0,
            ("zstd", _) => 4.0,
            ("lzma", <= 1) => 30.0,
            ("lzma", <= 5) => 4.0,
            ("lzma", _) => 2.5,
            ("deflate", <= 5) => 40.0,
            ("deflate", _) => 20.0,
            ("bzip2", _) => 12.0,
            _ => 10.0,
        };

        // Sıkıştırılmış veride kodlayıcı eşleşme bulamaz ve daha hızlı geçer.
        if (kind == Kind.Compressed)
            perCore *= 1.8;

        // LZMA2 ve zstd blok blok paralelleşir; zip dosya başına; bzip2 ve gzip tek akışta sınırlı.
        var scaling = Family(preset) switch
        {
            "lzma" or "zstd" => Math.Min(cores, 16) * 0.8,
            "deflate" when preset.Format == ArchiveFormat.Zip => Math.Min(cores, 16) * 0.7,
            "bzip2" => Math.Min(cores, 8) * 0.8,
            _ => 1,
        };
        return Math.Min(perCore * Math.Max(1, scaling), 1500);
    }

    private static string Family(CompressionPreset preset) => preset.Method.ToUpperInvariant() switch
    {
        "LZMA" or "LZMA2" or "PPMD" => "lzma",
        "ZSTD" => "zstd",
        "DEFLATE" or "DEFLATE64" => "deflate",
        "BZIP2" => "bzip2",
        _ => "other",
    };

    private static Dictionary<string, Kind> Build()
    {
        var map = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase);
        void Add(Kind kind, string extensions)
        {
            foreach (var extension in extensions.Split(' '))
                map["." + extension] = kind;
        }

        Add(Kind.Text, "txt md log csv tsv json xml html htm css js ts tsx jsx cs cpp c h hpp py java kt go rs rb php sql yml yaml ini " +
                       "toml cfg conf xaml csproj sln props targets shader hlsl glsl cginc svg srt vtt tex rtf ps1 sh bat cmd " +
                       "meta unity prefab asset mat anim controller uasset-txt");
        Add(Kind.Binary, "exe dll so dylib pdb obj o lib a msi sys bin dat db sqlite class wasm ttf otf uasset umap pak bundle");
        Add(Kind.RawMedia, "bmp tga tif tiff psd raw dng wav aiff exr hdr dds ico cur");
        Add(Kind.Compressed, "jpg jpeg png gif webp avif heic heif jxl mp3 m4a aac ogg opus flac mp4 m4v mkv webm mov avi wmv " +
                             "zip 7z rar gz tgz xz txz bz2 zst lz4 cab jar apk aab ipa docx xlsx pptx odt ods epub nupkg vsix pdf " +
                             "woff woff2 unitypackage");
        return map;
    }
}

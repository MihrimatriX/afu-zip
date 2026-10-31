namespace AfuZip.Core;

/// <summary>Yeni arşivin adı ve konumu: oluşturma diyaloğunun varsayılanı ve sağ tık "sıkıştır" komutları.</summary>
public static class ArchiveNaming
{
    private static readonly string[] CompoundExtensions = [".tar.gz", ".tar.xz", ".tar.bz2", ".tar.zst"];

    public static string ExtensionFor(CompressionPreset preset) => preset.TarWrapped
        ? preset.Format switch
        {
            ArchiveFormat.GZip => ".tar.gz",
            ArchiveFormat.Xz => ".tar.xz",
            ArchiveFormat.BZip2 => ".tar.bz2",
            ArchiveFormat.Zstd => ".tar.zst",
            _ => ".tar",
        }
        : preset.Format switch
        {
            ArchiveFormat.SevenZip => ".7z",
            ArchiveFormat.Zip => ".zip",
            ArchiveFormat.Tar => ".tar",
            ArchiveFormat.Wim => ".wim",
            var other => throw new NotSupportedException($"{other.DisplayName()} için arşiv oluşturulamaz."),
        };

    /// <summary>
    /// Tek kaynak: onun adı ("Belgeler" → Belgeler.7z, "rapor.docx" → rapor.7z); birden fazla kaynak: bulundukları
    /// klasörün adı. Arşiv kaynakların yanına yazılır. Kaynak yoksa <paramref name="fallbackFolder"/>/Arşiv.
    /// </summary>
    public static string DefaultTarget(IEnumerable<string> sources, string extension, string? fallbackFolder = null)
    {
        var list = sources.ToList();
        if (list.Count == 0)
            return Path.Combine(fallbackFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Arşiv" + extension);

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(list[0]));
        var folder = Path.GetDirectoryName(full) ?? full;
        var name = list.Count > 1
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))
            : Directory.Exists(full) ? Path.GetFileName(full) : Path.GetFileNameWithoutExtension(full);
        // "C:\" gibi bir kökün adı yoktur.
        if (string.IsNullOrWhiteSpace(name))
            name = "Arşiv";
        return Path.Combine(folder, SafePath.SanitizeSegment(name) + extension);
    }

    /// <summary>Aynı adda dosya (veya bölünmüş arşivin ilk cildi) varsa "ad (2).7z", "ad (3).7z"...</summary>
    public static string Unique(string path)
    {
        if (!Taken(path))
            return path;

        var extension = CompoundExtensions.FirstOrDefault(c => path.EndsWith(c, StringComparison.OrdinalIgnoreCase))
                        ?? Path.GetExtension(path);
        var stem = path[..^extension.Length];
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (!Taken(candidate))
                return candidate;
        }

        static bool Taken(string p) => File.Exists(p) || Directory.Exists(p) || File.Exists(p + ".001");
    }

    /// <summary>Uzantıyı değiştirir; ".tar.gz" gibi bileşik uzantıları tek parça sayar.</summary>
    public static string ReplaceExtension(string path, string extension)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;
        string[] known = [.. CompoundExtensions, ".tgz", ".txz", ".7z", ".zip", ".tar", ".wim"];
        var trimmed = known.FirstOrDefault(k => path.EndsWith(k, StringComparison.OrdinalIgnoreCase)) is { } match
            ? path[..^match.Length]
            : path;
        return trimmed + extension;
    }
}

using System.Text.RegularExpressions;

namespace AfuZip.Core;

/// <summary>
/// "Buraya akıllı çıkar": arşivin tek bir kök klasörü (veya tek dosyası) varsa doğrudan hedefe çıkarır;
/// birden fazla kök öğe varsa dağılmasın diye arşiv adıyla yeni bir klasör açar.
/// </summary>
public static partial class SmartExtract
{
    private static readonly string[] CompoundExtensions =
        [".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lz4", ".tar.br", ".tar.lzma", ".tar.z"];

    public static string ResolveDestination(string archiveFileName, IEnumerable<ArchiveEntry> entries, string baseDirectory)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (TopLevel(entry.Path) is { } top)
                roots.Add(top);
            if (roots.Count > 1)
                break;
        }

        if (roots.Count == 1)
        {
            var only = Path.Combine(baseDirectory, SafePath.SanitizeSegment(roots.First()));
            if (!File.Exists(only) && !Directory.Exists(only))
                return baseDirectory;
        }

        return UniqueDirectory(Path.Combine(baseDirectory, SafePath.SanitizeSegment(ArchiveStem(archiveFileName))));
    }

    /// <summary>"yedek.tar.gz" → "yedek", "film.part1.rar" → "film", "proje.7z.001" → "proje".</summary>
    public static string ArchiveStem(string archiveFileName)
    {
        var name = Path.GetFileName(archiveFileName);
        name = VolumeSuffix().Replace(name, "");

        foreach (var compound in CompoundExtensions)
        {
            if (name.EndsWith(compound, StringComparison.OrdinalIgnoreCase))
                return Fallback(name[..^compound.Length]);
        }

        return Fallback(Path.GetFileNameWithoutExtension(name));

        static string Fallback(string stem) => string.IsNullOrWhiteSpace(stem) ? "arşiv" : stem;
    }

    private static string? TopLevel(string path)
    {
        foreach (var segment in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment != ".")
                return segment;
        }
        return null;
    }

    private static string UniqueDirectory(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            return path;
        for (var i = 2; ; i++)
        {
            var candidate = $"{path} ({i})";
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
        }
    }

    // ".part1.rar", ".part01.rar" ve ".001" gibi cilt ekleri; ".7z.001"de önce .001, sonra .7z düşer.
    [GeneratedRegex(@"(\.part0*1\.rar|\.\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex VolumeSuffix();
}

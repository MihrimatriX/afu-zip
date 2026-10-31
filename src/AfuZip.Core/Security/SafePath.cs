using System.Buffers;

namespace AfuZip.Core;

/// <summary>
/// Arşivden gelen yolları güvenli göreli yollara çevirir. Kurallar her işletim sisteminde aynı
/// (Windows kuralları) uygulanır; böylece Linux'ta çalışan testler Windows davranışını doğrular.
/// </summary>
public static class SafePath
{
    private static readonly SearchValues<char> InvalidChars = SearchValues.Create("<>:\"|?*");

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// "a/b/../c" gibi kök dışına çıkabilecek yolları reddeder; geçersiz karakterleri, ADS ayracını (':'),
    /// sondaki nokta/boşlukları ve aygıt adlarını (CON, NUL...) güvenli hale getirir.
    /// </summary>
    /// <exception cref="UnsafePathException"></exception>
    public static string SanitizeRelative(string entryPath)
    {
        ArgumentNullException.ThrowIfNull(entryPath);

        var segments = entryPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var clean = new List<string>(segments.Length);
        foreach (var raw in segments)
        {
            if (raw == ".")
                continue;
            if (raw == "..")
                throw new UnsafePathException(entryPath, "üst klasöre çıkan '..' bileşeni");
            clean.Add(SanitizeSegment(raw));
        }

        if (clean.Count == 0)
            throw new UnsafePathException(entryPath, "boş yol");

        return string.Join(Path.DirectorySeparatorChar, clean);
    }

    public static string SanitizeSegment(string segment)
    {
        var chars = segment.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < 32 || InvalidChars.Contains(chars[i]))
                chars[i] = '_';
        }

        var s = new string(chars);

        // Windows sondaki nokta ve boşlukları sessizce siler: "a.txt." ile "a.txt" çakışır.
        var trimmed = s.TrimEnd('.', ' ');
        if (trimmed.Length != s.Length)
            s = trimmed + new string('_', s.Length - trimmed.Length);

        // "CON", "nul.txt", "COM1.log" gibi adlar dosya değil aygıt açar.
        var stem = s.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem))
            s = "_" + s;

        return s;
    }

    /// <summary>Girdinin hedef klasör içindeki tam yolunu döndürür; kök dışına çıkarsa istisna atar.</summary>
    /// <exception cref="UnsafePathException"></exception>
    public static string ResolveTarget(string destinationRoot, string entryPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, SanitizeRelative(entryPath)));

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root, comparison))
            throw new UnsafePathException(entryPath, "hedef klasörün dışına çıkıyor");

        return full;
    }
}

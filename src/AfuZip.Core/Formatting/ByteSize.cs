using System.Globalization;

namespace AfuZip.Core;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        if (bytes < 1024)
            return string.Create(provider, $"{bytes} B");

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(provider, $"{value:0.#} {Units[unit]}");
    }

    public static string FormatRatio(double? ratio) => ratio is { } r ? $"%{Math.Round(r * 100):0}" : "";

    /// <summary>"700m", "4g", "1,5 GB", "100k", "65536" → bayt (1024 tabanlı, 7z.exe -v ile aynı).</summary>
    public static long Parse(string text)
    {
        var s = text.Trim().Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant().Replace(',', '.');
        if (s.EndsWith('b'))
            s = s[..^1];
        var (number, multiplier) = s.Length > 0 && char.IsLetter(s[^1])
            ? (s[..^1], s[^1] switch
            {
                'k' => 1L << 10,
                'm' => 1L << 20,
                'g' => 1L << 30,
                't' => 1L << 40,
                _ => throw new FormatException($"Bilinmeyen birim: {text}"),
            })
            : (s, 1L);

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || value <= 0 || value * multiplier > long.MaxValue / 2)
            throw new FormatException($"Geçersiz boyut: {text}");
        return checked((long)Math.Round(value * multiplier));
    }
}

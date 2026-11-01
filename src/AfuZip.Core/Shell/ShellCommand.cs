namespace AfuZip.Core;

public enum ShellVerb
{
    /// <summary>Arşivi uygulamada aç (bayraksız çağrı, çift tıklama, "Birlikte aç").</summary>
    Open,
    /// <summary>Akıllı çıkarma: tek kök klasör varsa doğrudan, yoksa arşiv adıyla klasöre.</summary>
    ExtractHere,
    /// <summary>Her zaman arşiv adıyla yeni bir klasöre çıkar.</summary>
    ExtractToFolder,
    Test,
    /// <summary>Diyalog açmadan, bir presetle kaynakların yanına arşiv oluştur.</summary>
    Compress,
    /// <summary>"Arşive ekle…": oluşturma diyaloğunu kaynaklarla aç.</summary>
    AddDialog,
}

/// <summary>
/// Explorer bağlam menüsünden ve komut satırından gelen istek. Komut satırı biçimi:
/// <c>AfuZip.exe [--extract-here | --extract-folder | --test | --add | --compress &lt;preset&gt;] yol...</c>
/// Çok sayıda seçimde Explorer eklentisi yolları bir dosyaya yazar: <c>--paths-from liste.txt</c> (satır başına bir yol).
/// </summary>
public sealed record ShellCommand(ShellVerb Verb, IReadOnlyList<string> Paths, string? PresetId = null)
{
    private static readonly Dictionary<string, ShellVerb> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--extract-here"] = ShellVerb.ExtractHere,
        ["--extract-folder"] = ShellVerb.ExtractToFolder,
        ["--test"] = ShellVerb.Test,
        ["--compress"] = ShellVerb.Compress,
        ["--add"] = ShellVerb.AddDialog,
    };

    /// <summary>Pencere açmadan küçük bir ilerleme penceresinde yürüyen işler.</summary>
    public bool IsBackgroundAction => Verb is ShellVerb.ExtractHere or ShellVerb.ExtractToFolder or ShellVerb.Test or ShellVerb.Compress;

    /// <summary>Kaynakları dosya veya klasör olabilen komutlar (diğerleri yalnızca arşiv dosyası alır).</summary>
    public bool AcceptsFolders => Verb is ShellVerb.Compress or ShellVerb.AddDialog;

    public static ShellCommand Parse(IEnumerable<string> args)
    {
        var verb = ShellVerb.Open;
        string? preset = null;
        var paths = new List<string>();

        using var e = args.GetEnumerator();
        while (e.MoveNext())
        {
            var arg = e.Current;
            if (Flags.TryGetValue(arg, out var flag))
            {
                verb = flag;
                if (flag == ShellVerb.Compress && e.MoveNext())
                    preset = e.Current.ToLowerInvariant();
            }
            else if (arg.Equals("--paths-from", StringComparison.OrdinalIgnoreCase))
            {
                if (e.MoveNext())
                    paths.AddRange(ReadList(e.Current));
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                // Bilinmeyen bayrak (eski sürümden kalmış kayıt) yol sayılmaz.
            }
            else if (Normalize(arg) is { } path)
            {
                paths.Add(path);
            }
        }

        return new ShellCommand(verb, paths, preset);
    }

    /// <summary>
    /// Explorer çok seçimde klasik menü komutunu her öğe için ayrı çalıştırır; aynı türdeki sıkıştırma
    /// istekleri tek arşivde birleşsin diye art arda gelenler birleştirilir.
    /// </summary>
    public bool CanMergeWith(ShellCommand other) =>
        Verb is ShellVerb.Compress or ShellVerb.AddDialog && other.Verb == Verb && other.PresetId == PresetId;

    public ShellCommand MergeWith(ShellCommand other) =>
        this with { Paths = [.. Paths, .. other.Paths.Where(p => !Paths.Contains(p, StringComparer.OrdinalIgnoreCase))] };

    /// <summary>
    /// Eklentinin yazdığı seçim listesi. %TEMP%\AfuZip altındaysa okunduktan sonra silinir; başka yerdeki
    /// listeye (kullanıcının kendi dosyası) dokunulmaz.
    /// </summary>
    private static IEnumerable<string> ReadList(string listPath)
    {
        var list = Normalize(listPath);
        if (list is null || !File.Exists(list))
            return [];
        string[] lines;
        try
        {
            lines = File.ReadAllLines(list);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "AfuZip") + Path.DirectorySeparatorChar;
        if (list.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(list);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return lines.Select(Normalize).OfType<string>().ToList();
    }

    private static string? Normalize(string arg)
    {
        var trimmed = arg.Trim().Trim('"');
        if (trimmed.Length == 0)
            return null;
        try
        {
            var full = Path.GetFullPath(trimmed);
            // "C:\" kök olarak kalır; "C:\Belgeler\" → "C:\Belgeler".
            return Path.GetPathRoot(full) == full ? full : Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

namespace AfuZip.Core;

public sealed record SourceItem(string FullPath, string EntryName, bool IsDirectory, long Size);

/// <summary>Sıkıştırılacak dosya ve klasörleri arşiv içi adlarıyla ('/' ayraçlı) listeler.</summary>
public static class SourceCollector
{
    public static IReadOnlyList<SourceItem> Collect(IEnumerable<string> sources)
    {
        var items = new List<SourceItem>();
        foreach (var source in sources)
        {
            var full = Path.GetFullPath(source);
            if (File.Exists(full))
            {
                items.Add(new(full, Path.GetFileName(full), false, new FileInfo(full).Length));
            }
            else if (Directory.Exists(full))
            {
                var trimmed = Path.TrimEndingDirectorySeparator(full);
                var baseDir = Path.GetDirectoryName(trimmed) ?? trimmed;
                items.Add(new(trimmed, Relative(baseDir, trimmed), true, 0));

                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var path in Directory.EnumerateFileSystemEntries(trimmed, "*", options).Order(StringComparer.Ordinal))
                {
                    var isDir = Directory.Exists(path);
                    items.Add(new(path, Relative(baseDir, path), isDir, isDir ? 0 : new FileInfo(path).Length));
                }
            }
            else
            {
                throw new FileNotFoundException($"Kaynak bulunamadı: {source}", source);
            }
        }
        return items;
    }

    private static string Relative(string baseDir, string path) =>
        Path.GetRelativePath(baseDir, path).Replace('\\', '/');
}

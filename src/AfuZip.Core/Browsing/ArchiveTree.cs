using System.Globalization;

namespace AfuZip.Core;

public enum SortColumn
{
    Name,
    Size,
    PackedSize,
    Ratio,
    Method,
    Modified,
}

/// <summary>Bir klasörün altındaki tüm dosyaların toplamı.</summary>
public sealed record FolderStats(int Files, long Size, long PackedSize);

/// <summary>Listede bir satır: gerçek veya örtük klasör ya da dosya.</summary>
public sealed record TreeItem(string Path, string Name, bool IsFolder, ArchiveEntry? Entry, FolderStats? Stats)
{
    public long Size => IsFolder ? Stats?.Size ?? 0 : Entry?.Size ?? 0;
    public long? PackedSize => IsFolder ? Stats is { PackedSize: > 0 } s ? s.PackedSize : null : Entry?.PackedSize;
    public DateTimeOffset? Modified => Entry?.Modified;
    public string? Method => IsFolder ? null : Entry?.Method;
    public double? Ratio => PackedSize is { } packed && Size > 0 ? (double)packed / Size : null;
}

/// <summary>Sol paneldeki klasör ağacının bir düğümü.</summary>
public sealed class FolderNode(string path, string name)
{
    public string Path { get; } = path;
    public string Name { get; } = name;
    public List<FolderNode> Children { get; } = [];
}

/// <summary>
/// Düz girdi listesinden klasör ağacı kurar. Arşivlerde klasör girdileri çoğu zaman yoktur;
/// "a/b/c.txt" varsa "a" ve "a/b" örtük klasör olarak eklenir. Klasör boyutları önceden toplanır.
/// </summary>
public sealed class ArchiveTree
{
    private readonly IReadOnlyList<ArchiveEntry> _entries;
    private readonly Dictionary<string, HashSet<string>> _subfolders = new(StringComparer.Ordinal) { [""] = [] };
    private readonly Dictionary<string, List<ArchiveEntry>> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ArchiveEntry> _folderEntries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Files, long Size, long Packed)> _stats = new(StringComparer.Ordinal);
    private readonly CultureInfo _culture;
    private readonly StringComparer _nameOrder;

    public ArchiveTree(IReadOnlyList<ArchiveEntry> entries, CultureInfo? culture = null)
    {
        _entries = entries;
        _culture = culture ?? CultureInfo.CurrentCulture;
        _nameOrder = StringComparer.Create(_culture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

        foreach (var entry in entries)
        {
            var path = entry.Path.Trim('/');
            if (path.Length == 0)
                continue;

            if (entry.IsDirectory)
            {
                EnsureFolder(path);
                _folderEntries[path] = entry;
                continue;
            }

            var parent = Parent(path);
            EnsureFolder(parent);
            if (!_files.TryGetValue(parent, out var list))
                _files[parent] = list = [];
            list.Add(entry);

            // Dosyanın boyutunu bütün üst klasörlere (kök dahil) ekle.
            for (var folder = parent; ; folder = Parent(folder))
            {
                var s = _stats.GetValueOrDefault(folder);
                _stats[folder] = (s.Files + 1, s.Size + entry.Size, s.Packed + (entry.PackedSize ?? 0));
                if (folder.Length == 0)
                    break;
            }
        }

        Root = BuildNode("", "");
    }

    public FolderNode Root { get; }

    public int FolderCount => _subfolders.Count - 1;

    public bool FolderExists(string folder) => _subfolders.ContainsKey(folder);

    public FolderStats StatsOf(string folder) =>
        _stats.TryGetValue(folder, out var s) ? new(s.Files, s.Size, s.Packed) : new(0, 0, 0);

    public IReadOnlyList<TreeItem> Children(string folder, SortColumn sort = SortColumn.Name, bool descending = false)
    {
        var items = new List<TreeItem>();
        if (_subfolders.TryGetValue(folder, out var subfolders))
        {
            foreach (var path in subfolders)
                items.Add(new TreeItem(path, NameOf(path), true, _folderEntries.GetValueOrDefault(path), StatsOf(path)));
        }
        if (_files.TryGetValue(folder, out var files))
        {
            foreach (var entry in files)
                items.Add(new TreeItem(entry.Path, entry.Name, false, entry, null));
        }

        items.Sort(Comparer(sort, descending));
        return items;
    }

    /// <summary>Ada göre (büyük/küçük harf ve Türkçe kurallarına duyarlı) arar; klasörler ve dosyalar.</summary>
    public IReadOnlyList<TreeItem> Search(string query, int limit = 5000)
    {
        query = query.Trim();
        if (query.Length == 0)
            return [];

        var compare = _culture.CompareInfo;
        var results = new List<TreeItem>();
        foreach (var folder in _subfolders.Keys)
        {
            if (folder.Length > 0 && compare.IndexOf(NameOf(folder), query, CompareOptions.IgnoreCase) >= 0)
                results.Add(new TreeItem(folder, NameOf(folder), true, _folderEntries.GetValueOrDefault(folder), StatsOf(folder)));
        }
        foreach (var entry in _entries)
        {
            if (results.Count >= limit)
                break;
            if (!entry.IsDirectory && compare.IndexOf(entry.Name, query, CompareOptions.IgnoreCase) >= 0)
                results.Add(new TreeItem(entry.Path, entry.Name, false, entry, null));
        }

        results.Sort(Comparer(SortColumn.Name, false));
        return results.Count > limit ? results[..limit] : results;
    }

    /// <summary>Seçili dosyaların ve seçili klasörlerin altındaki her şeyin girdi indekslerini artan sırada döndürür.</summary>
    public IReadOnlyList<int> CollectIndices(IEnumerable<TreeItem> selection)
    {
        var indices = new SortedSet<int>();
        var prefixes = new List<string>();
        foreach (var item in selection)
        {
            if (item.IsFolder)
                prefixes.Add(item.Path.Trim('/') + "/");
            else if (item.Entry is { } entry)
                indices.Add(entry.Index);
        }

        if (prefixes.Count > 0)
        {
            foreach (var entry in _entries)
            {
                var path = entry.Path.Trim('/') + "/";
                foreach (var prefix in prefixes)
                {
                    if (path.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        indices.Add(entry.Index);
                        break;
                    }
                }
            }
        }

        return [.. indices];
    }

    public static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private Comparison<TreeItem> Comparer(SortColumn sort, bool descending)
    {
        return (a, b) =>
        {
            // Klasörler her zaman üstte; sıralama yönü yalnızca grup içinde uygulanır.
            if (a.IsFolder != b.IsFolder)
                return a.IsFolder ? -1 : 1;

            var result = sort switch
            {
                SortColumn.Size => a.Size.CompareTo(b.Size),
                SortColumn.PackedSize => Nullable.Compare(a.PackedSize, b.PackedSize),
                SortColumn.Ratio => Nullable.Compare(a.Ratio, b.Ratio),
                SortColumn.Method => _nameOrder.Compare(a.Method ?? "", b.Method ?? ""),
                SortColumn.Modified => Nullable.Compare(a.Modified, b.Modified),
                _ => 0,
            };
            if (result == 0)
                result = _nameOrder.Compare(a.Name, b.Name);
            return descending ? -result : result;
        };
    }

    private void EnsureFolder(string folder)
    {
        while (folder.Length > 0)
        {
            var parent = Parent(folder);
            if (!_subfolders.TryGetValue(parent, out var siblings))
                _subfolders[parent] = siblings = [];
            siblings.Add(folder);
            _subfolders.TryAdd(folder, []);
            folder = parent;
        }
    }

    private FolderNode BuildNode(string path, string name)
    {
        var node = new FolderNode(path, name);
        if (_subfolders.TryGetValue(path, out var children))
        {
            foreach (var child in children.OrderBy(NameOf, _nameOrder))
                node.Children.Add(BuildNode(child, NameOf(child)));
        }
        return node;
    }
}

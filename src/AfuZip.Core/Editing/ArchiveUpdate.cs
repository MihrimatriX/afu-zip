namespace AfuZip.Core;

/// <summary>Var olan bir arşivde yapılacak değişiklikler. Motor bunu yeni bir arşiv olarak yazar ve eskisinin yerine koyar.</summary>
public sealed record ArchiveUpdate
{
    /// <summary>Silinecek dosya veya klasör yolları (arşiv içi, '/' ayraçlı). Klasör silinince altındaki her şey gider.</summary>
    public IReadOnlyCollection<string> Delete { get; init; } = [];

    /// <summary>Yol → yeni ad (yalnızca son parça). Klasör yeniden adlandırılınca altındaki her şey taşınır.</summary>
    public IReadOnlyDictionary<string, string> Rename { get; init; } = new Dictionary<string, string>();

    /// <summary>Eklenecek dosya ve klasörler (diskteki yollar).</summary>
    public IReadOnlyList<string> Add { get; init; } = [];

    /// <summary>Eklenenlerin konacağı arşiv içi klasör ("" = kök).</summary>
    public string AddToFolder { get; init; } = "";

    /// <summary>Eklenen dosya arşivde aynı yolda varsa ne yapılacağı.</summary>
    public ConflictPolicy OnConflict { get; init; } = ConflictPolicy.Replace;

    /// <summary>Eklenen dosyalar için sıkıştırma ayarı; null ise arşivin biçimine göre varsayılan.</summary>
    public CompressionPreset? Preset { get; init; }

    /// <summary>Eklenen dosyaları şifrelemek için (7z, zip); şifreli arşivi yeniden yazmak için de gerekebilir.</summary>
    public string? Password { get; init; }

    public bool IsEmpty => Delete.Count == 0 && Rename.Count == 0 && Add.Count == 0;
}

public enum ConflictPolicy
{
    /// <summary>Arşivdeki dosya yenisiyle değiştirilir.</summary>
    Replace,

    /// <summary>Arşivdeki kalır, yenisi eklenmez.</summary>
    Skip,

    /// <summary>Çakışma hata verir.</summary>
    Fail,
}

/// <summary>Yeni arşivdeki bir girdi: eskiden aynen, eskiden yeni adla veya diskten.</summary>
public abstract record UpdateItem(string Path)
{
    /// <summary>Eski arşivdeki girdi, sıkıştırılmış verisiyle birlikte aynen kopyalanır (yeniden sıkıştırma yok).</summary>
    public sealed record Keep(int Index, string Path) : UpdateItem(Path);

    /// <summary>Veri aynen kopyalanır, yalnızca yol değişir.</summary>
    public sealed record Rename(int Index, string Path) : UpdateItem(Path);

    /// <summary>Diskten yeni dosya veya klasör.</summary>
    public sealed record Add(SourceItem Source, string Path) : UpdateItem(Path);
}

/// <summary>
/// <see cref="ArchiveUpdate"/>'i girdi listesine çevirir: silinenler çıkar, yeniden adlandırılanlar taşınır,
/// eklenenler sona gelir. Saf mantık; motordan bağımsız test edilir.
/// </summary>
public static class ArchiveUpdatePlanner
{
    public static IReadOnlyList<UpdateItem> Plan(IReadOnlyList<ArchiveEntry> entries, ArchiveUpdate update,
        IReadOnlyList<SourceItem>? sources = null)
    {
        var allPaths = entries.Select(e => Normalize(e.Path)).ToList();
        var deletes = update.Delete.Select(Normalize).Where(p => p.Length > 0).ToList();
        // Olmayan bir yolu silmek sessizce "silindi" demesin (yazım hatası).
        foreach (var delete in deletes)
        {
            if (!allPaths.Any(p => IsSameOrUnder(p, delete)))
                throw new FileNotFoundException($"Arşivde bulunamadı: {delete}");
        }
        var renames = BuildRenames(allPaths, update.Rename);

        var items = new List<UpdateItem>(entries.Count);
        foreach (var entry in entries)
        {
            var path = Normalize(entry.Path);
            if (deletes.Any(d => IsSameOrUnder(path, d)))
                continue;

            var renamed = ApplyRenames(path, renames);
            items.Add(renamed == path ? new UpdateItem.Keep(entry.Index, path) : new UpdateItem.Rename(entry.Index, renamed));
        }

        if (update.Add.Count > 0 || sources is { Count: > 0 })
            AddSources(items, sources ?? SourceCollector.Collect(update.Add), Normalize(update.AddToFolder), update.OnConflict);

        return items;
    }

    /// <summary>Eklenecek dosyalardan arşivde zaten bulunanların yolları (kullanıcıya sormak için).</summary>
    public static IReadOnlyList<string> FindConflicts(IReadOnlyList<ArchiveEntry> entries, IReadOnlyList<SourceItem> sources, string folder)
    {
        var existing = entries.Where(e => !e.IsDirectory).Select(e => Normalize(e.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        folder = Normalize(folder);
        return [.. sources.Where(s => !s.IsDirectory)
            .Select(s => Normalize(folder.Length == 0 ? s.EntryName : $"{folder}/{s.EntryName}"))
            .Where(existing.Contains)];
    }

    private static void AddSources(List<UpdateItem> items, IReadOnlyList<SourceItem> sources, string folder, ConflictPolicy policy)
    {
        // Önce birebir eşleşme; yoksa Windows'taki gibi büyük/küçük harf duyarsız. ("A.txt" ve "a.txt" ikisi de varsa
        // "a.txt" eklemek yanlışlıkla "A.txt"yi silmesin.)
        var exact = new Dictionary<string, int>(StringComparer.Ordinal);
        var loose = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            exact.TryAdd(items[i].Path, i);
            loose.TryAdd(items[i].Path, i);
        }

        var removed = new HashSet<int>();
        var added = new List<UpdateItem>();
        var addedIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var path = Normalize(folder.Length == 0 ? source.EntryName : $"{folder}/{source.EntryName}");
            ValidatePath(path);

            // Aynı ada sahip iki kaynak (C:\a\x.txt ve D:\b\x.txt): sonraki kazanır, arşivde iki kopya olmaz.
            if (addedIndex.TryGetValue(path, out var earlier))
            {
                if (source.IsDirectory && added[earlier] is UpdateItem.Add { Source.IsDirectory: true })
                    continue;
                added[earlier] = new UpdateItem.Add(source, path);
                continue;
            }

            if ((exact.TryGetValue(path, out var index) || loose.TryGetValue(path, out index)) && !removed.Contains(index))
            {
                var isDir = IsDirectoryItem(items[index], items);
                // Var olan klasöre klasör eklemek: klasör girdisi zaten var, içerik ayrı ayrı eklenir.
                if (source.IsDirectory && isDir)
                    continue;
                switch (policy)
                {
                    case ConflictPolicy.Skip:
                        continue;
                    case ConflictPolicy.Fail:
                        throw new IOException($"Arşivde aynı adda bir öğe var: {path}");
                }
                removed.Add(index);
            }

            addedIndex[path] = added.Count;
            added.Add(new UpdateItem.Add(source, path));
        }

        if (removed.Count > 0)
        {
            var kept = items.Where((_, i) => !removed.Contains(i)).ToList();
            items.Clear();
            items.AddRange(kept);
        }
        items.AddRange(added);
    }

    private static bool IsDirectoryItem(UpdateItem item, List<UpdateItem> items) =>
        item is UpdateItem.Add { Source.IsDirectory: true } ||
        items.Any(other => other.Path.StartsWith(item.Path + "/", StringComparison.OrdinalIgnoreCase));

    private static List<(string From, string To)> BuildRenames(List<string> allPaths, IReadOnlyDictionary<string, string> renames)
    {
        var result = new List<(string, string)>();

        foreach (var (rawFrom, newName) in renames)
        {
            var from = Normalize(rawFrom);
            if (!allPaths.Any(p => IsSameOrUnder(p, from)))
                throw new FileNotFoundException($"Arşivde bulunamadı: {from}");
            ValidateName(newName);

            var slash = from.LastIndexOf('/');
            var to = slash < 0 ? newName : $"{from[..slash]}/{newName}";
            if (string.Equals(from, to, StringComparison.Ordinal))
                continue;

            // Yalnızca büyük/küçük harf değişiyorsa çakışma değildir.
            if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) &&
                allPaths.Any(p => IsSameOrUnder(p, to, StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"Arşivde aynı adda bir öğe var: {to}");

            result.Add((from, to));
        }
        return result;
    }

    private static string ApplyRenames(string path, List<(string From, string To)> renames)
    {
        foreach (var (from, to) in renames)
        {
            if (path == from)
                return to;
            if (path.StartsWith(from + "/", StringComparison.Ordinal))
                return to + path[from.Length..];
        }
        return path;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException($"Geçersiz ad: \"{name}\"");
        if (SafePath.SanitizeSegment(name) != name)
            throw new ArgumentException($"\"{name}\" Windows'ta dosya adı olarak kullanılamaz.");
    }

    private static void ValidatePath(string path)
    {
        foreach (var segment in path.Split('/'))
            ValidateName(segment);
    }

    private static bool IsSameOrUnder(string path, string folder, StringComparison comparison = StringComparison.Ordinal) =>
        path.Equals(folder, comparison) || path.StartsWith(folder + "/", comparison);

    private static string Normalize(string path) => ArchiveEntry.NormalizePath(path).TrimEnd('/');
}

using AfuZip.Core;

namespace AfuZip.App.ViewModels;

/// <summary>Listede bir satırın görünümü: <see cref="TreeItem"/>'ı biçimlendirir.</summary>
public sealed class EntryItem(TreeItem item, bool showFullPath = false)
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".heic", ".avif", ".svg" };
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".mp3", ".wav", ".flac", ".ogg", ".m4a" };
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".tsx", ".js", ".json", ".xml", ".xaml", ".py", ".cpp", ".h", ".hlsl", ".shader", ".html", ".css", ".md" };

    public TreeItem Item => item;
    public ArchiveEntry? Entry => item.Entry;
    public bool IsFolder => item.IsFolder;
    public string Path => item.Path;
    public string Name => showFullPath ? item.Path.TrimEnd('/') : item.Name;
    public bool IsArchive => item.Entry is { } e && NestedArchive.LooksLikeArchive(e);

    // Segoe Fluent Icons
    public string Glyph
    {
        get
        {
            if (IsFolder) return "";
            if (item.Entry?.IsEncrypted == true) return "";
            if (IsArchive) return "";
            var ext = System.IO.Path.GetExtension(item.Name);
            if (ImageExtensions.Contains(ext)) return "";
            if (MediaExtensions.Contains(ext)) return "";
            if (CodeExtensions.Contains(ext)) return "";
            return "";
        }
    }

    public string SizeText => IsFolder
        ? item.Stats is { Files: > 0 } s ? ByteSize.Format(s.Size) : ""
        : ByteSize.Format(item.Size);

    public string PackedText => item.PackedSize is { } packed ? ByteSize.Format(packed) : "";
    public string RatioText => ByteSize.FormatRatio(item.Ratio);
    public string MethodText => IsFolder ? item.Stats is { Files: > 0 } s ? $"{s.Files:N0} dosya" : "" : item.Method ?? "";
    public string ModifiedText => item.Modified?.LocalDateTime.ToString("g") ?? "";
    public string ToolTip => item.Path;

    /// <summary>Ekran okuyucular ve UI Automation liste öğesini bu adla okur (yoksa sınıf adı okunur).</summary>
    public override string ToString() => Name;
}

/// <summary>Breadcrumb'da bir parça: hangi seviyedeki (iç içe arşiv) hangi klasör.</summary>
public sealed record BreadcrumbSegment(string Label, int Level, string Folder)
{
    public override string ToString() => Label;
}

/// <summary>Sol paneldeki klasör ağacının görünüm modeli.</summary>
public sealed class FolderNodeViewModel(FolderNode node, string? label = null, bool expanded = false)
{
    public string Name { get; } = label ?? node.Name;
    public string Path => node.Path;
    public bool IsExpanded { get; } = expanded;
    public List<FolderNodeViewModel> Children { get; } = node.Children.Select(c => new FolderNodeViewModel(c)).ToList();
}

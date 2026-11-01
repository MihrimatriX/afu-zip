using System.Globalization;
using System.Text.Json;
using AfuZip.Core;

namespace AfuZip.App.Services;

public sealed record RecentItem(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string SizeText => new FileInfo(Path) is { Exists: true } info ? ByteSize.Format(info.Length) : "";

    /// <summary>Bugün değiştiyse saat, bu yıl ise gün ve ay, daha eskiyse tam tarih.</summary>
    public string DateText
    {
        get
        {
            var info = new FileInfo(Path);
            if (!info.Exists)
                return "";
            var time = info.LastWriteTime;
            var tr = CultureInfo.GetCultureInfo("tr-TR");
            return time.Date == DateTime.Today ? time.ToString("HH:mm", tr)
                : time.Year == DateTime.Today.Year ? time.ToString("d MMM HH:mm", tr)
                : time.ToString("d MMM yyyy", tr);
        }
    }
}

/// <summary>Son açılan arşivler: %LOCALAPPDATA%\AfuZip\recent.json (en fazla 15 kayıt).</summary>
public sealed class RecentArchives
{
    private const int Capacity = 15;

    private readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AfuZip", "recent.json");

    private List<string> _paths = [];

    public IReadOnlyList<RecentItem> Load()
    {
        try
        {
            if (File.Exists(_file))
                _paths = JsonSerializer.Deserialize(File.ReadAllText(_file), RecentJsonContext.Default.ListString) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _paths = [];
        }

        _paths = _paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(Capacity).ToList();
        return Items;
    }

    public IReadOnlyList<RecentItem> Items => _paths.Select(p => new RecentItem(p)).ToList();

    public IReadOnlyList<RecentItem> Add(string path)
    {
        _paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _paths.Insert(0, path);
        if (_paths.Count > Capacity)
            _paths.RemoveRange(Capacity, _paths.Count - Capacity);
        Save();
        return Items;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_paths, RecentJsonContext.Default.ListString));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(List<string>))]
internal sealed partial class RecentJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

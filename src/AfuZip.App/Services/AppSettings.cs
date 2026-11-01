using System.Text.Json;
using System.Text.Json.Serialization;

namespace AfuZip.App.Services;

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

public enum PreviewPlacement
{
    Bottom,
    Right,
    Hidden,
}

public enum ExtractHereMode
{
    /// <summary>Tek kök klasör varsa doğrudan, yoksa arşiv adıyla klasöre.</summary>
    Smart,
    AlwaysFolder,
}

/// <summary>Kullanıcı ayarları: %LOCALAPPDATA%\AfuZip\settings.json.</summary>
public sealed class AppSettings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    public PreviewPlacement Preview { get; set; } = PreviewPlacement.Bottom;
    public bool ShowFolderTree { get; set; }
    public bool CompactRows { get; set; } = true;
    public double PreviewHeight { get; set; } = 300;
    public double PreviewWidth { get; set; } = 460;
    public double TreeWidth { get; set; } = 220;
    public string DefaultPreset { get; set; } = "balanced";
    public bool OpenArchiveWhenCreated { get; set; } = true;
    public ExtractHereMode ExtractHere { get; set; } = ExtractHereMode.Smart;
    public bool RevealAfterExtract { get; set; }
    public bool Notifications { get; set; } = true;
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Ayarların tek kaynağı. Değişiklik kaydedilir ve <see cref="Changed"/> ile pencerelere duyurulur.</summary>
public static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AfuZip", "settings.json");

    public static AppSettings Current { get; } = Load();

    /// <summary>Görünümü etkileyen bir ayar değişti (tema, önizleme yeri, ağaç, satır yüksekliği).</summary>
    public static event Action? Changed;

    /// <param name="notify">false: yalnızca kaydet (ör. ayraç sürüklenince panel boyu), pencereleri yeniden düzenleme.</param>
    public static void Update(Action<AppSettings> change, bool notify = true)
    {
        change(Current);
        Save();
        if (notify)
            Changed?.Invoke();
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.AppSettings) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Error("Ayarlar okunamadı; varsayılanlar kullanılıyor", ex);
        }
        return new();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Ayarlar kaydedilemedi", ex);
        }
    }
}

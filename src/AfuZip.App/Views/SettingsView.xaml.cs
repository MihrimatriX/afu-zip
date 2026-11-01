using System.Diagnostics;
using System.Reflection;
using AfuZip.App.Services;
using AfuZip.Core;
using AfuZip.Engine.SevenZip;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AfuZip.App.Views;

/// <summary>Ayarlar sekmesi. Her değişiklik anında kaydedilir; görünüm ayarları açık pencerelere hemen uygulanır.</summary>
public sealed partial class SettingsView : UserControl
{
    private readonly SevenZipEngine _engine;

    public SettingsView(SevenZipEngine engine)
    {
        _engine = engine;
        InitializeComponent();

        var s = SettingsStore.Current;
        ThemeBox.SelectedIndex = (int)s.Theme;
        TreeToggle.IsOn = s.ShowFolderTree;
        CompactToggle.IsOn = s.CompactRows;
        foreach (var preset in CompressionPreset.All)
            PresetBox.Items.Add($"{preset.DisplayName}: {preset.Description}");
        PresetBox.SelectedIndex = Math.Max(0, CompressionPreset.All.ToList().FindIndex(p => p.Id == s.DefaultPreset));
        OpenCreatedToggle.IsOn = s.OpenArchiveWhenCreated;
        ExtractModeBox.SelectedIndex = (int)s.ExtractHere;
        RevealToggle.IsOn = s.RevealAfterExtract;
        ShellToggle.IsOn = ShellIntegration.IsRegistered;
        NotifyToggle.IsOn = s.Notifications;
        ShowEngine();
        VersionText.Text = $"AFU Zip {Version}";
        if (AppIcon.PngPath is { } png)
            AboutIcon.Source = new BitmapImage(new Uri(png));

        // Olaylar ilk değerler atandıktan sonra bağlanır; yoksa açılışta her biri "değişti" sayılırdı.
        ThemeBox.SelectionChanged += (_, _) => SettingsStore.Update(x => x.Theme = (ThemeChoice)ThemeBox.SelectedIndex);
        TreeToggle.Toggled += (_, _) => SettingsStore.Update(x => x.ShowFolderTree = TreeToggle.IsOn);
        CompactToggle.Toggled += (_, _) => SettingsStore.Update(x => x.CompactRows = CompactToggle.IsOn);
        PresetBox.SelectionChanged += (_, _) =>
            SettingsStore.Update(x => x.DefaultPreset = CompressionPreset.All[Math.Max(0, PresetBox.SelectedIndex)].Id, notify: false);
        OpenCreatedToggle.Toggled += (_, _) => SettingsStore.Update(x => x.OpenArchiveWhenCreated = OpenCreatedToggle.IsOn, notify: false);
        ExtractModeBox.SelectionChanged += (_, _) =>
            SettingsStore.Update(x => x.ExtractHere = (ExtractHereMode)ExtractModeBox.SelectedIndex, notify: false);
        RevealToggle.Toggled += (_, _) => SettingsStore.Update(x => x.RevealAfterExtract = RevealToggle.IsOn, notify: false);
        NotifyToggle.Toggled += (_, _) => SettingsStore.Update(x => x.Notifications = NotifyToggle.IsOn, notify: false);
        ShellToggle.Toggled += OnShellToggled;
        DefaultAppsButton.Click += async (_, _) =>
        {
            if (!ShellIntegration.IsRegistered)
                ShellToggle.IsOn = true;
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps?registeredAppUser=AFU%20Zip"));
        };
        EngineFolderButton.Click += (_, _) =>
        {
            if (_engine.LibraryPath is { } path)
                Notifications.Reveal(path);
        };
        LogFolderButton.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppLog.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppLog.Folder}\"") { UseShellExecute = true });
        };
    }

    private static string Version =>
        typeof(SettingsView).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "";

    private void ShowEngine()
    {
        if (_engine.LibraryPath is { } path)
        {
            var writable = _engine.Formats.Count(f => f.CanUpdate);
            EngineTitle.Text = $"7-Zip bulundu: {_engine.Formats.Count} biçim açılır, {writable} biçimde arşiv oluşturulur";
            EngineText.Text = path + (_engine.HasZstd
                ? "\nZstandard ile sıkıştırma var (7-Zip ZS)."
                : "\nZstandard ile sıkıştırma yok: \"Hızlı\" LZMA2'ye düşer, tar.zst oluşturulamaz. 7-Zip ZS kurarsanız gelir.");
            EngineFolderButton.IsEnabled = true;
        }
        else
        {
            EngineTitle.Text = "7-Zip bulunamadı";
            EngineText.Text = "Yalnızca zip, tar ve tar.gz açılıp oluşturulabilir. 7z, RAR, ISO ve diğerleri için 7-Zip veya 7-Zip ZS kurun.";
            EngineFolderButton.IsEnabled = false;
        }
    }

    private void OnShellToggled(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ShellToggle.IsOn)
                ShellIntegration.Register();
            else
                ShellIntegration.Unregister();
            Show(InfoBarSeverity.Success, ShellToggle.IsOn
                ? "Explorer'a eklendi. Arşivlere ve dosyalara sağ tıklayın (Windows 11'de \"Daha fazla seçenek göster\")."
                : "Explorer sağ tık menüsünden kaldırıldı.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            AppLog.Error("Kabuk kaydı", ex);
            Show(InfoBarSeverity.Error, $"Kayıt defterine yazılamadı: {ex.Message}");
        }
    }

    private void Show(InfoBarSeverity severity, string message)
    {
        Notice.Severity = severity;
        Notice.Message = message;
        Notice.IsOpen = true;
    }
}

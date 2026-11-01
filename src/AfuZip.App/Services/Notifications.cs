using System.Diagnostics;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace AfuZip.App.Services;

/// <summary>
/// Windows bildirimleri: sağ tık işleri bitince "Klasörü aç" düğmeli bir bildirim. Bildirime uygulama kapandıktan
/// sonra tıklanırsa Windows uygulamayı bildirim aktivasyonuyla başlatır; o durumda yalnızca klasör açılır.
/// </summary>
internal static class Notifications
{
    private const string ActionKey = "action";
    private const string PathKey = "path";
    private const string RevealAction = "reveal";

    private static bool s_registered;

    public static void Initialize()
    {
        if (s_registered)
            return;
        try
        {
            // Olay, Register'dan önce bağlanmalı (belgelenmiş sıra).
            AppNotificationManager.Default.NotificationInvoked += (_, e) => Handle(e.Arguments);
            if (AppIcon.PngPath is { } icon)
                AppNotificationManager.Default.Register("AFU Zip", new Uri(icon));
            else
                AppNotificationManager.Default.Register();
            s_registered = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Bildirimler kapalı ya da desteklenmiyor: işler bildirimsiz sürer.
        }
    }

    public static void Show(string title, string message, string? revealPath = null, bool isError = false)
    {
        if (!s_registered || !SettingsStore.Current.Notifications)
            return;
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message);
            if (revealPath is not null)
            {
                builder.AddArgument(ActionKey, RevealAction).AddArgument(PathKey, revealPath);
                builder.AddButton(new AppNotificationButton(Directory.Exists(revealPath) ? "Klasörü aç" : "Dosyayı göster")
                    .AddArgument(ActionKey, RevealAction)
                    .AddArgument(PathKey, revealPath));
            }
            if (isError)
                builder.SetScenario(AppNotificationScenario.Urgent);
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <summary>Bildirim aktivasyonuyla başlatıldıysa işini yapar; true dönerse uygulama pencere açmadan kapanabilir.</summary>
    public static bool HandleLaunch(AppNotificationActivatedEventArgs args)
    {
        Handle(args.Arguments);
        return true;
    }

    public static void Reveal(string path)
    {
        try
        {
            var arguments = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private static void Handle(IDictionary<string, string> arguments)
    {
        if (arguments.TryGetValue(ActionKey, out var action) && action == RevealAction &&
            arguments.TryGetValue(PathKey, out var path) && (Directory.Exists(path) || File.Exists(path)))
        {
            Reveal(path);
        }
    }
}

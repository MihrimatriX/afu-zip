using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace AfuZip.App.Services;

/// <summary>Ayarlardaki temayı pencereye uygular (Sistem / Açık / Koyu).</summary>
internal static class WindowTheme
{
    public static ElementTheme Current => SettingsStore.Current.Theme switch
    {
        ThemeChoice.Light => ElementTheme.Light,
        ThemeChoice.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static void Apply(Window window)
    {
        var theme = Current;
        if (window.Content is FrameworkElement root)
            root.RequestedTheme = theme;

        // Simge durumuna küçült / kapat düğmelerini sistem çizer; uygulama teması sistemden farklıysa onlara da
        // söylenir (yoksa koyu temada siyah simgeler kalır).
        var bar = window.AppWindow.TitleBar;
        bar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }
}

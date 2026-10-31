using Microsoft.UI.Windowing;

namespace AfuZip.App.Services;

/// <summary>Uygulama simgesi: başlık çubuğu, görev çubuğu ve bildirimler için (Assets klasöründe, exe'nin yanında).</summary>
internal static class AppIcon
{
    public static string? IcoPath => Existing("AfuZip.ico");

    public static string? PngPath => Existing("AfuZip.png");

    public static void Apply(AppWindow window)
    {
        if (IcoPath is { } ico)
            window.SetIcon(ico);
    }

    private static string? Existing(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", name);
        return File.Exists(path) ? path : null;
    }
}

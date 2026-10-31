using System.Runtime.InteropServices;
using AfuZip.Core;
using Microsoft.Win32;

namespace AfuZip.App.Services;

/// <summary>
/// Explorer sağ tık menüsü, "Birlikte aç" ve Varsayılan uygulamalar kaydı (yalnızca geçerli kullanıcı, HKCU).
/// Ne yazılacağı <see cref="ShellRegistration"/>'da; burası yalnızca kayıt defterine uygular.
/// </summary>
internal static partial class ShellIntegration
{
    private const int ShcneAssocChanged = 0x08000000;

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AfuZip.exe");

    public static string? RegisteredExePath
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ShellRegistration.AppKey);
            return key?.GetValue(ShellRegistration.ExePathValue) as string;
        }
    }

    public static bool IsRegistered => RegisteredExePath is not null;

    public static void Register()
    {
        var exe = ExePath;
        foreach (var write in ShellRegistration.Build(exe))
        {
            using var key = Registry.CurrentUser.CreateSubKey(write.Key, writable: true);
            var kind = write.Kind == RegistryKind.DWord ? RegistryValueKind.DWord : RegistryValueKind.String;
            key.SetValue(write.Name ?? "", write.Value, kind);
        }
        NotifyShell();
    }

    public static void Unregister()
    {
        var exe = RegisteredExePath ?? ExePath;
        foreach (var path in ShellRegistration.OwnedKeys(exe))
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        foreach (var value in ShellRegistration.SharedValues())
        {
            using var key = Registry.CurrentUser.OpenSubKey(value.Key, writable: true);
            key?.DeleteValue(value.Name, throwOnMissingValue: false);
        }
        NotifyShell();
    }

    /// <summary>Uygulama klasörü taşındıysa (yeni sürüm başka yere açıldı) menü komutları yeni exe'ye yönlendirilir.</summary>
    public static void RefreshIfMoved()
    {
        try
        {
            if (RegisteredExePath is { } registered && !string.Equals(registered, ExePath, StringComparison.OrdinalIgnoreCase))
                Register();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }
    }

    private static void NotifyShell() => SHChangeNotify(ShcneAssocChanged, 0, 0, 0);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);
}

using System.Runtime.InteropServices;
using AfuZip.Core;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace AfuZip.App.Services;

/// <summary>
/// Explorer entegrasyonu (yalnızca geçerli kullanıcı, yönetici izni gerekmez):
/// <list type="bullet">
/// <item>Klasik sağ tık menüsü, "Birlikte aç" ve Varsayılan uygulamalar: HKCU kayıtları (<see cref="ShellRegistration"/>).</item>
/// <item>Windows 11'in yeni menüsü: yanındaki imzalı sparse paket (AfuZip.ContextMenu.msix) uygulama klasörüne
/// bağlanarak kurulur. Paket yoksa ya da imzası bu bilgisayarda güvenilir değilse atlanır; klasik menü yine çalışır.</item>
/// </list>
/// </summary>
internal static partial class ShellIntegration
{
    private const int ShcneAssocChanged = 0x08000000;
    private const string SparsePackageName = "AfuZip.ContextMenu";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AfuZip.exe");

    private static string AppFolder => Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;

    /// <summary>Explorer eklentisi (yalnızca derlenmiş pakette; geliştirme derlemesinde olmayabilir).</summary>
    private static string? HandlerDll =>
        Path.Combine(AppFolder, "AfuZip.ShellExtension.dll") is var dll && File.Exists(dll) ? dll : null;

    private static string SparsePackagePath => Path.Combine(AppFolder, "AfuZip.ContextMenu.msix");

    /// <summary>Windows 11 ve imzalı paket yanında: yeni menüye girilebilir.</summary>
    public static bool CanUseModernMenu =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && File.Exists(SparsePackagePath) && HandlerDll is not null;

    public static string? RegisteredExePath
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ShellRegistration.AppKey);
            return key?.GetValue(ShellRegistration.ExePathValue) as string;
        }
    }

    public static bool IsRegistered => RegisteredExePath is not null;

    /// <summary>Yeni menü paketi bu kullanıcı için kurulu mu.</summary>
    public static bool IsModernMenuRegistered
    {
        get
        {
            try
            {
                return FindSparsePackages().Any();
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Klasik menüyü yazar, mümkünse yeni menü paketini kurar. Dönen metin yeni menünün durumu (null: denenmedi).</summary>
    public static string? Register()
    {
        var exe = ExePath;
        RemoveRegistryEntries(RegisteredExePath ?? exe);
        foreach (var write in ShellRegistration.Build(exe, HandlerDll))
        {
            using var key = Registry.CurrentUser.CreateSubKey(write.Key, writable: true);
            var kind = write.Kind == RegistryKind.DWord ? RegistryValueKind.DWord : RegistryValueKind.String;
            key.SetValue(write.Name ?? "", write.Value, kind);
        }
        NotifyShell();
        return CanUseModernMenu ? RegisterModernMenu() : null;
    }

    /// <summary>Kayıttan sonra kullanıcıya gösterilecek metin.</summary>
    public static string RegisteredMessage(string? modernMenu) => modernMenu switch
    {
        null => "Explorer'a eklendi: arşivlere ve dosyalara sağ tıklayın (Windows 11'de \"Daha fazla seçenek göster\" altında).",
        _ when IsModernMenuRegistered => "Explorer'a eklendi: Windows 11'in sağ tık menüsünde \"AFU Zip\" görünür.",
        var status => $"Explorer'a eklendi (klasik menü). {status}",
    };

    public static void Unregister()
    {
        RemoveRegistryEntries(RegisteredExePath ?? ExePath);
        NotifyShell();
        UnregisterModernMenu();
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

    private static void RemoveRegistryEntries(string exe)
    {
        foreach (var path in ShellRegistration.OwnedKeys(exe))
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        foreach (var value in ShellRegistration.SharedValues())
        {
            using var key = Registry.CurrentUser.OpenSubKey(value.Key, writable: true);
            key?.DeleteValue(value.Name, throwOnMissingValue: false);
        }
    }

    // --- Windows 11 yeni menü (sparse paket) ------------------------------------------------------

    private static IEnumerable<Windows.ApplicationModel.Package> FindSparsePackages() =>
        new PackageManager().FindPackagesForUser(string.Empty).Where(p => p.Id.Name == SparsePackageName);

    /// <summary>
    /// Paket her seferinde yeniden kurulur: dış konum (uygulama klasörü) kurulum anında sabitlenir, klasör
    /// taşındıysa eski kayıt yanlış yeri gösterir. Sonuç Explorer yeniden başlayınca ya da birkaç saniye içinde görünür.
    /// </summary>
    private static string RegisterModernMenu()
    {
        try
        {
            // PackageManager işlemleri arka plan thread'inde beklenir (arayüz thread'inden çağrılabilir).
            return Task.Run(async () =>
            {
                var manager = new PackageManager();
                foreach (var old in FindSparsePackages().ToList())
                    await manager.RemovePackageAsync(old.Id.FullName);

                var options = new AddPackageOptions
                {
                    ExternalLocationUri = new Uri(AppFolder + Path.DirectorySeparatorChar),
                };
                var result = await manager.AddPackageByUriAsync(new Uri(SparsePackagePath), options);
                if (result.ExtendedErrorCode is { } error)
                    throw error;
                return "Windows 11 menüsüne eklendi.";
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Çoğunlukla: paket imzası bu bilgisayarda güvenilir değil (0x800B0109) ya da yan yükleme kapalı.
            AppLog.Error("Windows 11 menü paketi kurulamadı", ex);
            return $"Windows 11 menüsüne eklenemedi ({AppLog.Describe(ex)}); klasik menü çalışıyor.";
        }
    }

    private static void UnregisterModernMenu()
    {
        try
        {
            Task.Run(async () =>
            {
                var manager = new PackageManager();
                foreach (var package in FindSparsePackages().ToList())
                    await manager.RemovePackageAsync(package.Id.FullName);
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Error("Windows 11 menü paketi kaldırılamadı", ex);
        }
    }

    private static void NotifyShell() => SHChangeNotify(ShcneAssocChanged, 0, 0, 0);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);
}

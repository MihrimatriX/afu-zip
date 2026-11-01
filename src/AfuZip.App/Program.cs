using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace AfuZip.App;

/// <summary>
/// Tek örnek (single instance) giriş noktası. İkinci örnek (çift tıklama, sağ tık komutu, bildirim) aktivasyonunu
/// çalışan örneğe iletip kapanır; böylece art arda gelen sağ tık komutları tek pencerede toplanır.
/// </summary>
public static partial class Program
{
    private const string InstanceKey = "afuzip-main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Kurulum programı ve betikler için: pencere açmadan Explorer kaydı.
        if (args is [var only] && only.Equals("--register", StringComparison.OrdinalIgnoreCase))
            return RunQuietly(() => Services.ShellIntegration.Register());
        if (args is [var single] && single.Equals("--unregister", StringComparison.OrdinalIgnoreCase))
            return RunQuietly(Services.ShellIntegration.Unregister);

        if (RedirectToRunningInstance())
            return 0;

        Application.Start(startParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static int RunQuietly(Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Services.AppLog.Error("Komut satırından kabuk kaydı", ex);
            return 1;
        }
    }

    private static bool RedirectToRunningInstance()
    {
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent)
        {
            main.Activated += (_, e) => App.OnRedirected(e);
            return false;
        }

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();

        // Belgelenmiş desen: yönlendirme arka plan thread'inde yapılır, STA thread'i COM mesajlarını
        // pompalayarak bekler; aksi halde RedirectActivationToAsync kilitlenebilir.
        var done = CreateEventW(0, bManualReset: true, bInitialState: false, null);
        Task.Run(() =>
        {
            main.RedirectActivationToAsync(activation).AsTask().Wait();
            SetEvent(done);
        });
        _ = CoWaitForMultipleObjects(0, uint.MaxValue, 1, [done], out _);
        CloseHandle(done);
        return true;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateEventW(nint lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        string? lpName);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(nint hEvent);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("ole32.dll")]
    private static partial int CoWaitForMultipleObjects(uint dwFlags, uint dwTimeout, uint cHandles, nint[] pHandles, out uint lpdwindex);
}

internal static class CommandLine
{
    /// <summary>Tırnak farkındalıklı basit argüman ayırıcı.</summary>
    public static IEnumerable<string> Split(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            yield break;

        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    /// <summary>Yönlendirilen aktivasyonun argüman dizisi programın kendi yolunu da içerir; o atlanır.</summary>
    public static IEnumerable<string> WithoutExecutable(IEnumerable<string> args)
    {
        var first = true;
        foreach (var arg in args)
        {
            if (first && Path.GetFileNameWithoutExtension(arg).Equals("AfuZip", StringComparison.OrdinalIgnoreCase) &&
                (arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || arg.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                first = false;
                continue;
            }
            first = false;
            yield return arg;
        }
    }
}

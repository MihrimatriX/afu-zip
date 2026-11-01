using AfuZip.App.Services;
using AfuZip.App.ViewModels;
using AfuZip.App.Views;
using AfuZip.Core;
using AfuZip.Engine.Managed;
using AfuZip.Engine.SevenZip;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace AfuZip.App;

/// <summary>
/// İki tür pencere: arşivlerin açıldığı ana pencere ve sağ tık işleri (buraya çıkar, sıkıştır) için küçük
/// ilerleme penceresi. İkisi de gerektiğinde oluşturulur; hiçbiri açık değilse uygulama kapanır.
/// </summary>
public partial class App : Application
{
    // Explorer çok seçimde klasik menü komutunu her dosya için ayrı süreçte başlatır; bu sürede gelen
    // sıkıştırma istekleri tek arşivde toplanır.
    private static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(1200);

    // Çok seçimde ikinci süreç, ilki OnLaunched'a varmadan yönlendirebilir; o istekler kaybolmasın diye bekletilir.
    private static readonly Lock s_gate = new();
    private static readonly Queue<AppActivationArguments> s_early = new();
    private static App? s_ready;

    private readonly IHost _host;
    private MainWindow? _main;
    private ProgressWindow? _progress;
    private Window? _lastActive;
    private ShellCommand? _pending;
    private DispatcherQueueTimer? _mergeTimer;
    private DispatcherQueue? _dispatcher;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => AppLog.Error("Yakalanmamış istisna", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("Gözlenmeyen görev istisnası", e.Exception);

        var builder = Host.CreateApplicationBuilder();
        // Kayıt sırası motor önceliğidir: önce 7z.dll, bulunamazsa yönetilen motor.
        builder.Services.AddSingleton<SevenZipEngine>();
        builder.Services.AddSingleton<IArchiveEngine>(sp => sp.GetRequiredService<SevenZipEngine>());
        builder.Services.AddSingleton<IArchiveEngine, ManagedEngine>();
        builder.Services.AddSingleton<EngineRouter>();
        builder.Services.AddSingleton<ArchiveOpener>();
        builder.Services.AddSingleton(new TaskQueue(maxConcurrency: 1));
        builder.Services.AddSingleton<UiPasswordProvider>();
        builder.Services.AddSingleton<RecentArchives>();
        builder.Services.AddSingleton<ShellActions>();
        _host = builder.Build();
    }

    private T Get<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Bildirim aktivasyonu ancak kayıttan sonra tanınır (belgelenmiş sıra: önce Register, sonra aktivasyon türü).
        Notifications.Initialize();

        // Uygulama kapandıktan sonra bildirimdeki "Klasörü aç"a tıklandı: pencere açmadan işi yap ve çık.
        if (AppInstance.GetCurrent().GetActivatedEventArgs() is { Kind: ExtendedActivationKind.AppNotification, Data: AppNotificationActivatedEventArgs notification })
        {
            Notifications.HandleLaunch(notification);
            Exit();
            return;
        }

        // Önceki oturumdan (çökme vb.) kalan geçici iç arşivleri temizle.
        _ = Task.Run(() =>
        {
            TempFolders.CleanupStale(TimeSpan.FromDays(1));
            // Kurulum programı güncellemede Explorer'ın kilitli tuttuğu eski eklentiyi yeniden adlandırır.
            foreach (var old in Directory.EnumerateFiles(AppContext.BaseDirectory, "AfuZip.ShellExtension.dll.eski*"))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        });

        ShellIntegration.RefreshIfMoved();

        var dispatcher = _dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = new TaskbarProgress(Get<TaskQueue>(), OpenWindowHandles);
        Get<UiPasswordProvider>().Attach(dispatcher, () => (_lastActive ?? (Window?)_progress ?? _main)?.Content?.XamlRoot);
        _mergeTimer = dispatcher.CreateTimer();
        _mergeTimer.Interval = MergeWindow;
        _mergeTimer.IsRepeating = false;
        _mergeTimer.Tick += (_, _) => FlushPending();

        Run(ShellCommand.Parse(Environment.GetCommandLineArgs().Skip(1)), coldStart: true);

        List<AppActivationArguments> early;
        lock (s_gate)
        {
            s_ready = this;
            early = [.. s_early];
            s_early.Clear();
        }
        foreach (var activation in early)
            OnRedirectedActivation(activation);
    }

    /// <summary>Çalışan örneğe başka bir süreçten gelen aktivasyon (arka plan thread'inde çağrılır).</summary>
    internal static void OnRedirected(AppActivationArguments args)
    {
        App? app;
        lock (s_gate)
        {
            app = s_ready;
            if (app is null)
            {
                s_early.Enqueue(args);
                return;
            }
        }
        app.OnRedirectedActivation(args);
    }

    /// <summary>İkinci bir örnek açıldığında (Explorer'dan çift tıklama, sağ tık komutu) istek bu örneğe gelir.</summary>
    private void OnRedirectedActivation(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.AppNotification && args.Data is AppNotificationActivatedEventArgs notification)
        {
            Notifications.HandleLaunch(notification);
            return;
        }
        if (args.Kind != ExtendedActivationKind.Launch || args.Data is not Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
            return;

        var command = ShellCommand.Parse(CommandLine.WithoutExecutable(CommandLine.Split(launch.Arguments)));
        _dispatcher?.TryEnqueue(() => Run(command, coldStart: false));
    }

    private void Run(ShellCommand command, bool coldStart)
    {
        var paths = command.Paths
            .Where(p => File.Exists(p) || (command.AcceptsFolders && Directory.Exists(p)))
            .Where(p => !string.Equals(p, ShellIntegration.ExePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        command = command with { Paths = paths };

        switch (command.Verb)
        {
            case ShellVerb.Open:
                var main = EnsureMain();
                main.BringToFront();
                _ = OpenAllAsync(main, paths);
                break;

            case ShellVerb.ExtractHere or ShellVerb.ExtractToFolder or ShellVerb.Test:
                foreach (var path in paths)
                {
                    StartJob(command.Verb == ShellVerb.Test
                        ? Get<ShellActions>().Test(path)
                        : Get<ShellActions>().Extract(path, toNewFolder: command.Verb == ShellVerb.ExtractToFolder));
                }
                break;

            case ShellVerb.Compress or ShellVerb.AddDialog when paths.Count > 0:
                if (_pending is { } pending && !pending.CanMergeWith(command))
                    FlushPending();
                _pending = _pending?.MergeWith(command) ?? command;
                _mergeTimer!.Stop();
                _mergeTimer.Start();
                return;
        }

        // Geçersiz bir komutla (dosya silinmiş) başlatıldıysa kapan; ama önce sıradaki yönlendirmeler işlensin.
        if (coldStart)
            _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, ExitIfIdle);
    }

    private static async Task OpenAllAsync(MainWindow main, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
            await main.ViewModel.OpenAsync(path);
    }

    private void FlushPending()
    {
        _mergeTimer?.Stop();
        if (_pending is not { } command)
            return;
        _pending = null;

        if (command.Verb == ShellVerb.AddDialog)
        {
            var main = EnsureMain();
            main.BringToFront();
            _ = main.ShowCreateDialogWhenReadyAsync(command.Paths);
        }
        else
        {
            StartJob(Get<ShellActions>().Compress(command.Paths, command.PresetId));
        }
    }

    private void StartJob(ShellJob job)
    {
        if (_progress is null)
        {
            _progress = new ProgressWindow();
            _progress.Activated += (_, e) => OnWindowActivated(_progress, e);
            _progress.Closed += (_, _) =>
            {
                _progress = null;
                _lastActive = _main;
            };
        }
        _progress.Add(job);
        _progress.Activate();
    }

    private MainWindow EnsureMain()
    {
        if (_main is not null)
            return _main;

        // Ana pencere kapanıp yeniden açılabildiği için (ilerleme penceresi açıkken) görünüm modeli her
        // pencereyle birlikte yeniden oluşturulur.
        var viewModel = ActivatorUtilities.CreateInstance<MainViewModel>(_host.Services);
        var window = new MainWindow(viewModel);
        window.Activated += (_, e) => OnWindowActivated(window, e);
        window.Closed += (_, _) =>
        {
            _main = null;
            _lastActive = _progress;
        };
        _main = window;
        window.Activate();
        return window;
    }

    private void OnWindowActivated(Window window, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != WindowActivationState.Deactivated)
            _lastActive = window;
    }

    private IEnumerable<nint> OpenWindowHandles()
    {
        if (_main is not null)
            yield return _main.Handle;
        if (_progress is not null)
            yield return _progress.Handle;
    }

    /// <summary>Komut geçersizse (dosya silinmiş) hiç pencere açılmamış olabilir; süreç boşta kalmasın.</summary>
    private void ExitIfIdle()
    {
        if (_main is null && _progress is null && _pending is null)
            Exit();
    }
}

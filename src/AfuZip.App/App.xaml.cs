using AfuZip.App.Services;
using AfuZip.App.ViewModels;
using AfuZip.Core;
using AfuZip.Core.Preview;
using AfuZip.Engine.Managed;
using AfuZip.Engine.SevenZip;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace AfuZip.App;

public partial class App : Application
{
    private readonly IHost _host;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

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
        // Tüm sekmeler tek önbelleği paylaşır: bütçe pencere başına (512 MB), sekme başına değil.
        builder.Services.AddSingleton(_ => new PreviewCache());
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        _host = builder.Build();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Önceki oturumdan (çökme vb.) kalan geçici iç arşivleri temizle.
        _ = Task.Run(() =>
        {
            TempFolders.CleanupStale(TimeSpan.FromDays(1));
            PreviewCache.CleanupStale();
        });

        _window = _host.Services.GetRequiredService<MainWindow>();
        _host.Services.GetRequiredService<UiPasswordProvider>()
            .Attach(_window.DispatcherQueue, () => _window.Content?.XamlRoot);
        _window.Activate();

        foreach (var path in CommandLine.ExistingFiles(Environment.GetCommandLineArgs().Skip(1)))
            _ = _window.ViewModel.OpenAsync(path);
    }

    /// <summary>İkinci bir örnek açıldığında (ör. Explorer'dan çift tıklama) argümanları bu pencereye yönlendirilir.</summary>
    internal void OnRedirectedActivation(AppActivationArguments args)
    {
        if (_window is null)
            return;

        var paths = args.Kind == ExtendedActivationKind.Launch &&
                    args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
            ? CommandLine.ExistingFiles(CommandLine.Split(launch.Arguments)).ToList()
            : [];

        _window.DispatcherQueue.TryEnqueue(async () =>
        {
            _window.BringToFront();
            foreach (var path in paths)
                await _window.ViewModel.OpenAsync(path);
        });
    }
}

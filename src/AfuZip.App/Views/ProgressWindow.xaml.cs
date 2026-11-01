using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using AfuZip.App.Services;
using AfuZip.App.ViewModels;
using AfuZip.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AfuZip.App.Views;

/// <summary>
/// Sağ tık işleri için küçük pencere. İşler başarıyla bitince bildirim gösterip kendiliğinden kapanır;
/// bir iş başarısız olursa hata okunabilsin diye açık kalır.
/// </summary>
public sealed partial class ProgressWindow : Window
{
    private const int WindowWidth = 560;
    private bool _closeConfirmed;

    public ProgressWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
        }
        AppIcon.Apply(AppWindow);
        if (AppIcon.PngPath is { } png)
            TitleIcon.Source = new BitmapImage(new Uri(png));
        WindowTheme.Apply(this);

        AppWindow.Closing += OnClosing;
        Resize();
        CenterOnScreen();
    }

    public ObservableCollection<ShellJob> Jobs { get; } = [];

    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public bool HasActiveJobs => Jobs.Any(j => j.Task.IsActive);

    public void Add(ShellJob job)
    {
        Jobs.Add(job);
        job.Task.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ArchiveTask.State))
                OnJobStateChanged(job);
        };
        Resize();
        UpdateSummary();
    }

    private async void OnJobStateChanged(ShellJob job)
    {
        UpdateSummary();
        switch (job.Task.State)
        {
            case ArchiveTaskState.Completed:
                Notifications.Show($"{job.Subject} {job.Verb}", job.Task.Message ?? "", job.RevealPath);
                break;
            case ArchiveTaskState.Failed:
                Notifications.Show($"{job.Subject}: işlem başarısız", job.Task.Message ?? "", isError: true);
                break;
            default:
                return;
        }

        // Hepsi başarıyla bittiyse kısa bir an %100'ü gösterip kapan; arada yeni iş gelirse açık kal.
        if (Jobs.All(j => j.Task.State is ArchiveTaskState.Completed or ArchiveTaskState.Canceled))
        {
            var count = Jobs.Count;
            await Task.Delay(1500);
            if (Jobs.Count == count && !HasActiveJobs && Jobs.All(j => j.Task.State != ArchiveTaskState.Failed))
            {
                _closeConfirmed = true;
                Close();
            }
        }
    }

    private void UpdateSummary()
    {
        var active = Jobs.Count(j => j.Task.IsActive);
        var failed = Jobs.Count(j => j.Task.State == ArchiveTaskState.Failed);
        Summary.Text = active > 0
            ? $"{active:N0} iş sürüyor"
            : failed > 0 ? $"{failed:N0} iş başarısız oldu" : "Tamamlandı";
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ShellJob { RevealPath: { } path })
            Notifications.Reveal(path);
    }

    private void OnPause(object sender, RoutedEventArgs e) => (((FrameworkElement)sender).Tag as ShellJob)?.Task.Pause();

    private void OnResume(object sender, RoutedEventArgs e) => (((FrameworkElement)sender).Tag as ShellJob)?.Task.Resume();

    private void OnCancel(object sender, RoutedEventArgs e) => (((FrameworkElement)sender).Tag as ShellJob)?.Task.Cancel();

    private async void OnClose(object sender, RoutedEventArgs e)
    {
        if (await ConfirmCloseAsync())
            Close();
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || !HasActiveJobs)
            return;
        args.Cancel = true;
        if (await ConfirmCloseAsync())
            Close();
    }

    /// <summary>Süren iş varsa sorar; onaylanırsa işleri iptal eder ve yarım dosyalar silinene kadar bekler.</summary>
    private async Task<bool> ConfirmCloseAsync()
    {
        if (HasActiveJobs)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "İşler iptal edilsin mi?",
                Content = "Süren işler durdurulacak; yarım kalan dosyalar silinir.",
                PrimaryButtonText = "İptal et ve kapat",
                CloseButtonText = "Devam etsin",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return false;

            var active = Jobs.Where(j => j.Task.IsActive).Select(j => j.Task).ToList();
            foreach (var task in active)
                task.Cancel();
            try
            {
                await Task.WhenAll(active.Select(t => t.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
            }
        }
        _closeConfirmed = true;
        return true;
    }

    private void Resize()
    {
        var scale = GetDpiForWindow(Handle) / 96.0;
        // Parola diyaloğu da bu pencerede açılır; tek işte bile ona yetecek yükseklik.
        var height = Math.Max(320, 120 + 104 * Math.Clamp(Jobs.Count, 1, 4));
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(WindowWidth * scale), (int)(height * scale)));
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}

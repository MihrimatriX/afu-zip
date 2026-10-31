using System.Collections.Specialized;
using AfuZip.App.Services;
using AfuZip.App.ViewModels;
using AfuZip.App.Views;
using AfuZip.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AfuZip.App;

public sealed partial class MainWindow : Window
{
    private bool _syncingSelection;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppIcon.Apply(AppWindow);
        if (AppIcon.PngPath is { } png)
        {
            TitleIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(png));
            WelcomeIcon.Source = TitleIcon.Source;
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 860));
        ShellSuggestion.IsOpen = !ShellIntegration.IsRegistered;

        // Sekme içerikleri kodda oluşturulur: her TabViewItem kendi ArchiveView'ını taşır.
        ViewModel.Tabs.CollectionChanged += OnTabsChanged;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedTab))
                SyncSelectedTab();
        };
        Closed += async (_, _) =>
        {
            ViewModel.Tabs.CollectionChanged -= OnTabsChanged;
            await ViewModel.CloseAllAsync();
        };
    }

    public MainViewModel ViewModel { get; }

    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    // --- Sekmeler --------------------------------------------------------------------------------

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ArchiveTabViewModel tab in e.OldItems)
            {
                var item = Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => t.Tag == tab);
                if (item is not null)
                {
                    (item.Content as ArchiveView)?.Close();
                    Tabs.TabItems.Remove(item);
                }
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ArchiveTabViewModel tab in e.NewItems)
            {
                var item = new TabViewItem
                {
                    Tag = tab,
                    Header = tab.Title,
                    IconSource = new FontIconSource { Glyph = "" },
                    Content = new ArchiveView(tab, () => Handle) { Margin = new Thickness(0, 8, 0, 8) },
                };
                ToolTipService.SetToolTip(item, tab.ArchivePath);
                Tabs.TabItems.Add(item);
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
            Tabs.TabItems.Clear();

        SyncSelectedTab();
    }

    private void SyncSelectedTab()
    {
        var item = Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => t.Tag == ViewModel.SelectedTab);
        if (item is null || ReferenceEquals(Tabs.SelectedItem, item))
            return;
        _syncingSelection = true;
        Tabs.SelectedItem = item;
        _syncingSelection = false;
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSelection && Tabs.SelectedItem is TabViewItem { Tag: ArchiveTabViewModel tab })
            ViewModel.SelectedTab = tab;
    }

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is ArchiveTabViewModel tab)
            await ViewModel.CloseTabAsync(tab);
    }

    private async void OnAddTabClick(TabView sender, object args) => await PickAndOpenAsync();

    private async void OnCloseTabAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.SelectedTab is { } tab)
            await ViewModel.CloseTabAsync(tab);
    }

    // --- Açma ------------------------------------------------------------------------------------

    private async void OnOpenClick(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private async void OnOpenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PickAndOpenAsync();
    }

    private async void OnRecentClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentItem recent)
            await ViewModel.OpenAsync(recent.Path);
    }

    private async Task PickAndOpenAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
        picker.FileTypeFilter.Add("*");
        foreach (var file in await picker.PickMultipleFilesAsync())
            await ViewModel.OpenAsync(file.Path);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) && !e.DataView.Contains(ArchiveView.InternalDragFormat))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Aç veya arşivle";
        }
    }

    /// <summary>Arşivler açılır; arşiv olmayan dosya ve klasörlerle "arşiv oluştur" diyaloğu açılır.</summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(ArchiveView.InternalDragFormat))
            return;

        var deferral = e.GetDeferral();
        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await e.DataView.GetStorageItemsAsync();
        }
        finally
        {
            deferral.Complete();
        }

        var toPack = new List<string>();
        foreach (var item in items)
        {
            if (item is StorageFile file && IsArchiveFile(file.Path))
                await ViewModel.OpenAsync(file.Path);
            else if (!string.IsNullOrEmpty(item.Path))
                toPack.Add(item.Path);
        }

        if (toPack.Count > 0)
            await ShowCreateDialogAsync(toPack);
    }

    // Zip tabanlı belge ve paket türleri sürüklenince açılmaz, arşivlenir (Word belgesi "arşiv" sayılmasın).
    private static readonly HashSet<string> ContainerDocuments = new(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".xlsx", ".pptx", ".epub", ".apk", ".jar", ".nupkg", ".vsix" };

    private static bool IsArchiveFile(string path) =>
        NestedArchive.IsFirstVolume(path) ||
        (FormatSniffer.FromExtension(path) != ArchiveFormat.Unknown && !ContainerDocuments.Contains(Path.GetExtension(path)));

    // --- Oluşturma -------------------------------------------------------------------------------

    private async void OnCreateClick(object sender, RoutedEventArgs e) => await ShowCreateDialogAsync([]);

    private async void OnCreateAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ShowCreateDialogAsync([]);
    }

    /// <summary>Sağ tık "Arşive ekle…": pencere yeni açıldıysa içerik yüklenene kadar beklenir (diyalog XamlRoot ister).</summary>
    internal async Task ShowCreateDialogWhenReadyAsync(IReadOnlyList<string> sources)
    {
        if (Content.XamlRoot is null && Content is FrameworkElement root)
        {
            var loaded = new TaskCompletionSource();
            root.Loaded += (_, _) => loaded.TrySetResult();
            await loaded.Task;
        }
        await ShowCreateDialogAsync(sources);
    }

    private async Task ShowCreateDialogAsync(IEnumerable<string> sources)
    {
        var viewModel = ViewModel.NewCreateArchive(sources);
        if (await CreateArchivePanel.ShowAsync(Content.XamlRoot, () => Handle, viewModel) is { } request)
            ViewModel.CreateArchive(request, viewModel.OpenWhenDone);
    }

    // --- Windows ile bütünleşme ------------------------------------------------------------------

    private void OnSettingsOpening(object? sender, object e)
    {
        ShellToggle.Toggled -= OnShellToggled;
        ShellToggle.IsOn = ShellIntegration.IsRegistered;
        ShellToggle.Toggled += OnShellToggled;
    }

    private void OnShellToggled(object sender, RoutedEventArgs e) => SetShellIntegration(ShellToggle.IsOn);

    private void OnSuggestionRegister(object sender, RoutedEventArgs e) => SetShellIntegration(true);

    private void SetShellIntegration(bool enabled)
    {
        try
        {
            if (enabled)
                ShellIntegration.Register();
            else
                ShellIntegration.Unregister();
            ShellSuggestion.IsOpen = false;
            ViewModel.SetStatus(enabled
                ? "Explorer'a eklendi: arşivlere ve dosyalara sağ tıklayın (Windows 11'de \"Daha fazla seçenek göster\")."
                : "Explorer sağ tık menüsünden kaldırıldı.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            ViewModel.SetStatus($"Kayıt defterine yazılamadı: {ex.Message}");
        }
    }

    private async void OnOpenDefaultApps(object sender, RoutedEventArgs e)
    {
        if (!ShellIntegration.IsRegistered)
            SetShellIntegration(true);
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps?registeredAppUser=AFU%20Zip"));
    }

    // --- Görevler --------------------------------------------------------------------------------

    private void OnClearTasks(object sender, RoutedEventArgs e) => ViewModel.ClearFinishedTasks();

    private void OnPauseTask(object sender, RoutedEventArgs e) => (((FrameworkElement)sender).Tag as ArchiveTask)?.Pause();

    private void OnResumeTask(object sender, RoutedEventArgs e) => (((FrameworkElement)sender).Tag as ArchiveTask)?.Resume();

    private void OnCancelTask(object sender, RoutedEventArgs e) => (((FrameworkElement)sender).Tag as ArchiveTask)?.Cancel();
}

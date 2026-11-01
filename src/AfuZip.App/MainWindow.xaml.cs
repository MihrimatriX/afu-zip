using System.Collections.Specialized;
using AfuZip.App.Services;
using AfuZip.App.ViewModels;
using AfuZip.App.Views;
using AfuZip.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace AfuZip.App;

/// <summary>
/// Sekme şeridi başlık çubuğunda; sekmelerin görünümleri TabView'ın dışında, <c>ViewHost</c>'ta tutulur
/// (sekme değişince kaydırma konumu ve seçim kaybolmasın). Hiç sekme yokken başlangıç ekranı görünür.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string SettingsTag = "settings";

    private readonly Dictionary<object, FrameworkElement> _views = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _toastTimer;
    private bool _syncingSelection;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppIcon.Apply(AppWindow);
        if (AppIcon.PngPath is { } png)
        {
            TitleIcon.Source = new BitmapImage(new Uri(png));
            HomeIcon.Source = TitleIcon.Source;
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        ShellSuggestion.IsOpen = !ShellIntegration.IsRegistered && !SettingsStore.Current.HideShellSuggestion;
        EngineWarning.IsOpen = !ViewModel.Engine.IsAvailable;
        Root.KeyboardAccelerators.Add(Accelerator(VirtualKeyModifiers.Control, (VirtualKey)188, OpenSettings)); // Ctrl+,

        WindowTheme.Apply(this);
        SettingsStore.Changed += OnSettingsChanged;
        Root.Loaded += (_, _) => ReserveCaptionButtons();
        Root.SizeChanged += (_, _) => ReserveCaptionButtons();

        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => Toast.IsOpen = false;
        ViewModel.Notice += ShowToast;

        ViewModel.Tabs.CollectionChanged += OnTabsChanged;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedTab))
                SyncSelectedTab();
        };
        Closed += async (_, _) =>
        {
            SettingsStore.Changed -= OnSettingsChanged;
            ViewModel.Notice -= ShowToast;
            _toastTimer.Stop();
            ViewModel.Tabs.CollectionChanged -= OnTabsChanged;
            foreach (var view in _views.Values.OfType<ArchiveView>())
                view.Close();
            await ViewModel.CloseAllAsync();
        };
        ShowView(null);
    }

    public MainViewModel ViewModel { get; }

    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private void OnSettingsChanged() => WindowTheme.Apply(this);

    /// <summary>Durum çubuğunun yerine: alt ortada birkaç saniyelik bildirim. Hatalar daha uzun kalır.</summary>
    private void ShowToast(string message, NoticeKind kind)
    {
        Toast.Severity = kind switch
        {
            NoticeKind.Success => InfoBarSeverity.Success,
            NoticeKind.Warning => InfoBarSeverity.Warning,
            NoticeKind.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        Toast.Message = message;
        Toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(kind is NoticeKind.Error or NoticeKind.Warning ? 9 : 4);
        _toastTimer.Start();
    }

    /// <summary>Simge durumuna küçült / kapat düğmeleri sekmelerin üstüne binmesin.</summary>
    private void ReserveCaptionButtons()
    {
        var scale = Root.XamlRoot?.RasterizationScale ?? 1;
        TitleArea.Padding = new Thickness(0, 0, AppWindow.TitleBar.RightInset / scale, 0);
    }

    // --- Sekmeler ve görünümler ------------------------------------------------------------------

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ArchiveTabViewModel tab in e.OldItems)
                RemoveTab(tab);
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
                };
                ToolTipService.SetToolTip(item, tab.ArchivePath);
                AddView(tab, new ArchiveView(tab, () => Handle));
                Tabs.TabItems.Add(item);
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var item in Tabs.TabItems.OfType<TabViewItem>().Where(t => t.Tag is ArchiveTabViewModel).ToList())
                RemoveTab((ArchiveTabViewModel)item.Tag);
        }

        SyncSelectedTab();
    }

    private void AddView(object key, FrameworkElement view)
    {
        view.Visibility = Visibility.Collapsed;
        _views[key] = view;
        ViewHost.Children.Insert(ViewHost.Children.IndexOf(Home) + 1, view); // meşgul çubuğu ve bildirim üstte kalsın
    }

    private void RemoveTab(object key)
    {
        if (Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => Equals(t.Tag, key)) is { } item)
            Tabs.TabItems.Remove(item);
        if (_views.Remove(key, out var view))
        {
            (view as ArchiveView)?.Close();
            ViewHost.Children.Remove(view);
        }
        if (Tabs.TabItems.Count == 0)
            ShowView(null);
    }

    private void ShowView(object? key)
    {
        foreach (var (viewKey, view) in _views)
            view.Visibility = Equals(viewKey, key) ? Visibility.Visible : Visibility.Collapsed;
        Home.Visibility = key is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncSelectedTab()
    {
        if (ViewModel.SelectedTab is not { } selected)
        {
            if (Tabs.TabItems.Count == 0)
                ShowView(null);
            return;
        }
        var item = Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => t.Tag == selected);
        if (item is null)
            return;
        ShowView(selected);
        if (ReferenceEquals(Tabs.SelectedItem, item))
            return;
        _syncingSelection = true;
        Tabs.SelectedItem = item;
        _syncingSelection = false;
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
            return;
        switch (Tabs.SelectedItem)
        {
            case TabViewItem { Tag: ArchiveTabViewModel tab }:
                ViewModel.SelectedTab = tab;
                ShowView(tab);
                break;
            case TabViewItem { Tag: SettingsTag }:
                ViewModel.SelectedTab = null;
                ShowView(SettingsTag);
                break;
            default:
                ShowView(null);
                break;
        }
    }

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) => await CloseTabAsync(args.Tab);

    private async Task CloseTabAsync(TabViewItem? item)
    {
        switch (item?.Tag)
        {
            case ArchiveTabViewModel tab:
                await ViewModel.CloseTabAsync(tab);
                break;
            case SettingsTag:
                RemoveTab(SettingsTag);
                break;
        }
    }

    private async void OnAddTabClick(TabView sender, object args) => await PickAndOpenAsync();

    private async void OnCloseTabAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await CloseTabAsync(Tabs.SelectedItem as TabViewItem);
    }

    // --- Ayarlar -----------------------------------------------------------------------------------

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>Ayarlar bir sekmede açılır (Windows Terminal gibi); zaten açıksa ona geçilir.</summary>
    public void OpenSettings()
    {
        if (Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => Equals(t.Tag, SettingsTag)) is not { } item)
        {
            item = new TabViewItem { Tag = SettingsTag, Header = "Ayarlar", IconSource = new FontIconSource { Glyph = "" } };
            AddView(SettingsTag, new SettingsView(ViewModel.Engine));
            Tabs.TabItems.Add(item);
        }
        Tabs.SelectedItem = item;
    }

    private static KeyboardAccelerator Accelerator(VirtualKeyModifiers modifiers, VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Modifiers = modifiers, Key = key };
        accelerator.Invoked += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        return accelerator;
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

    private void OnSuggestionRegister(object sender, RoutedEventArgs e)
    {
        try
        {
            ShellIntegration.Register();
            ShellSuggestion.IsOpen = false;
            ShowToast("Explorer'a eklendi: arşivlere ve dosyalara sağ tıklayın (Windows 11'de \"Daha fazla seçenek göster\").", NoticeKind.Success);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            AppLog.Error("Kabuk kaydı", ex);
            ShowToast($"Kayıt defterine yazılamadı: {ex.Message}", NoticeKind.Error);
        }
    }

    /// <summary>Öneri kapatıldıysa bir daha gösterilmez (Ayarlar'dan her zaman eklenebilir).</summary>
    private void OnSuggestionClosed(InfoBar sender, object args) =>
        SettingsStore.Update(x => x.HideShellSuggestion = true, notify: false);

    // --- Görevler --------------------------------------------------------------------------------

    private void OnClearTasks(object sender, RoutedEventArgs e) => ViewModel.ClearFinishedTasks();

    private void OnPauseTask(object sender, RoutedEventArgs e) => (((FrameworkElement)sender).Tag as ArchiveTask)?.Pause();

    private void OnResumeTask(object sender, RoutedEventArgs e) => (((FrameworkElement)sender).Tag as ArchiveTask)?.Resume();

    private void OnCancelTask(object sender, RoutedEventArgs e) => (((FrameworkElement)sender).Tag as ArchiveTask)?.Cancel();
}

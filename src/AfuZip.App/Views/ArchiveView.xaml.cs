using System.ComponentModel;
using AfuZip.App.Services;
using AfuZip.App.ViewModels;
using AfuZip.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace AfuZip.App.Views;

/// <summary>Bir sekmenin içeriği. Görünüm modeli kurucuda verilir; tüm x:Bind'ler ona bağlanır.</summary>
public sealed partial class ArchiveView : UserControl
{
    /// <summary>Uygulamanın kendi sürüklemesi: listeye veya pencereye geri bırakılınca "ekle/aç" sayılmasın.</summary>
    internal const string InternalDragFormat = "AfuZip.InternalDrag";

    private const double MinPreviewWidth = 260;
    private const double MinPreviewHeight = 140;
    private const double MinListWidth = 360;
    private const double MinListHeight = 120;
    private const double MinTreeWidth = 140;
    private const double MaxTreeWidth = 480;

    private readonly Func<nint> _windowHandle;
    private readonly PreviewPane _preview;
    private PreviewPlacement _previewSetting = SettingsStore.Current.Preview;
    private bool _previewRight;
    private DependencyObject? _previewDefinition; // alttaysa satır, sağdaysa sütun tanımı
    private bool _closed;

    public ArchiveView(ArchiveTabViewModel viewModel, Func<nint> windowHandle)
    {
        ViewModel = viewModel;
        _windowHandle = windowHandle;
        InitializeComponent();

        PreviewHost.Child = _preview = new PreviewPane(viewModel.Preview);
        PreviewSplitter.Dragged += OnPreviewSplitterDragged;
        PreviewSplitter.DragCompleted += OnPreviewSplitterCompleted;
        TreeSplitter.Dragged += OnTreeSplitterDragged;
        TreeSplitter.DragCompleted += () => SettingsStore.Update(x => x.TreeWidth = TreeColumn.Width.Value, notify: false);

        ViewModel.PropertyChanged += OnViewModelChanged;
        SettingsStore.Changed += OnSettingsChanged;
        // Gizli sekmedeyken başka sekmede değişen panel boyutları, sekmeye dönünce uygulanır.
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible)
                ApplyLayout();
        });
        // Pencere küçülünce liste sıfıra inmesin.
        ContentArea.SizeChanged += (_, _) => ClampPreview();
        ApplyLayout();
    }

    /// <summary>Sekme kapanırken: önizlemenin WebView2 sürecini bırak, ayar olaylarından çık.</summary>
    public void Close()
    {
        if (_closed)
            return;
        _closed = true;
        SettingsStore.Changed -= OnSettingsChanged;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        _preview.Close();
    }

    public ArchiveTabViewModel ViewModel { get; }

    // --- Çıkarma ve test -------------------------------------------------------------------------

    private void OnExtractHere(object sender, RoutedEventArgs e) => ViewModel.ExtractHere(ExtractScope.All);

    private void OnExtractSelectionHere(object sender, RoutedEventArgs e) => ViewModel.ExtractHere(ExtractScope.Selection);

    private async void OnExtractSelectionTo(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } folder)
            ViewModel.ExtractTo(ExtractScope.Selection, folder);
    }

    private async void OnExtractAllTo(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } folder)
            ViewModel.ExtractTo(ExtractScope.All, folder);
    }

    private void OnTest(object sender, RoutedEventArgs e) => ViewModel.Test();

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    // --- Gezinme ---------------------------------------------------------------------------------

    private async void OnUp(object sender, RoutedEventArgs e) => await ViewModel.GoUpAsync();

    private async void OnBreadcrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is BreadcrumbSegment segment)
            await ViewModel.NavigateToSegmentAsync(segment);
    }

    private void OnFolderInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is FolderNodeViewModel node)
            ViewModel.Navigate(node.Path);
    }

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<SortColumn>(tag, out var column))
            ViewModel.SortBy(column);
    }

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await ViewModel.SelectAsync(EntriesList.SelectedItems.OfType<EntryItem>().ToList());

    private async void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is EntryItem item)
            await ViewModel.ActivateAsync(item);
    }

    private async void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when EntriesList.SelectedItem is EntryItem item:
                e.Handled = true;
                await ViewModel.ActivateAsync(item);
                break;
            case VirtualKey.Back:
                e.Handled = true;
                await ViewModel.GoUpAsync();
                break;
            // Del ve F2 yalnızca liste odaktayken: arama kutusunda Del metni silmeli, dosyayı değil.
            case VirtualKey.Delete when ViewModel.CanDelete:
                e.Handled = true;
                await ConfirmDeleteAsync();
                break;
            case VirtualKey.F2 when ViewModel.CanRename && EntriesList.SelectedItem is EntryItem renamed:
                e.Handled = true;
                await PromptRenameAsync(renamed);
                break;
        }
    }

    // --- Düzenleme -------------------------------------------------------------------------------

    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
            await AddWithConfirmationAsync([.. files.Select(f => f.Path)]);
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } folder)
            await AddWithConfirmationAsync([folder]);
    }

    /// <summary>Arşivde aynı adlı dosya varsa sessizce değiştirmek yerine sorar: değiştir, atla veya vazgeç.</summary>
    private async Task AddWithConfirmationAsync(IReadOnlyList<string> paths)
    {
        if (!ViewModel.CanEdit)
            return;

        IReadOnlyList<string> conflicts;
        try
        {
            conflicts = await ViewModel.FindAddConflictsAsync(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await new ContentDialog { XamlRoot = XamlRoot, Title = "Eklenemedi", Content = ex.Message, CloseButtonText = "Tamam" }.ShowAsync();
            return;
        }

        var policy = ConflictPolicy.Replace;
        if (conflicts.Count > 0)
        {
            var list = string.Join("\n", conflicts.Take(8)) + (conflicts.Count > 8 ? $"\n… ve {conflicts.Count - 8:N0} dosya daha" : "");
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = conflicts.Count == 1 ? "Bu dosya arşivde zaten var" : $"{conflicts.Count:N0} dosya arşivde zaten var",
                Content = new ScrollViewer
                {
                    MaxHeight = 240,
                    Content = new TextBlock { Text = list, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                },
                PrimaryButtonText = "Değiştir",
                SecondaryButtonText = "Atla",
                CloseButtonText = "Vazgeç",
                DefaultButton = ContentDialogButton.Secondary,
            };
            policy = await dialog.ShowAsync() switch
            {
                ContentDialogResult.Primary => ConflictPolicy.Replace,
                ContentDialogResult.Secondary => ConflictPolicy.Skip,
                _ => (ConflictPolicy)(-1),
            };
            if (!Enum.IsDefined(policy))
                return;
        }

        ViewModel.Add(paths, policy);
    }

    private async void OnDelete(object sender, RoutedEventArgs e) => await ConfirmDeleteAsync();

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is EntryItem item)
            await PromptRenameAsync(item);
    }

    private async Task ConfirmDeleteAsync()
    {
        var selected = EntriesList.SelectedItems.OfType<EntryItem>().ToList();
        if (selected.Count == 0)
            return;

        var what = selected.Count == 1 ? $"\"{selected[0].Item.Name}\"" : $"{selected.Count:N0} öğe";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Arşivden silinsin mi?",
            Content = $"{what} arşivden kalıcı olarak silinecek. Bu işlem geri alınamaz.",
            PrimaryButtonText = "Sil",
            CloseButtonText = "Vazgeç",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.DeleteSelection();
    }

    private async Task PromptRenameAsync(EntryItem item)
    {
        var name = item.Item.Name;
        var box = new TextBox { Text = name, AcceptsReturn = false };
        // Explorer gibi: uzantı hariç adı seç.
        box.Loaded += (_, _) =>
        {
            var dot = item.IsFolder ? -1 : name.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : name.Length);
            box.Focus(FocusState.Programmatic);
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Yeniden adlandır",
            Content = box,
            PrimaryButtonText = "Yeniden adlandır",
            CloseButtonText = "Vazgeç",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0 && box.Text.Trim() != name;
        dialog.IsPrimaryButtonEnabled = false;

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.Rename(item, box.Text.Trim());
    }

    // --- Dışarı sürükleme ve dışarıda düzenleme ---------------------------------------------------

    /// <summary>
    /// Dosyalar sürükleme başlarken değil, Explorer'a bırakıldığı anda çıkarılır (gecikmeli veri): vazgeçilen
    /// sürüklemede hiçbir şey çıkarılmaz, büyük dosyalarda sürükleme takılmaz.
    /// </summary>
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<EntryItem>().ToList();
        if (items.Count == 0 || !ViewModel.HasArchive)
        {
            e.Cancel = true;
            return;
        }

        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetData(InternalDragFormat, ViewModel.ArchivePath);
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var storage = new List<IStorageItem>();
                foreach (var path in await ViewModel.ExtractForDragAsync(items))
                {
                    storage.Add(Directory.Exists(path)
                        ? await StorageFolder.GetFolderFromPathAsync(path)
                        : await StorageFile.GetFileFromPathAsync(path));
                }
                request.SetData(storage);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArchivePasswordException)
            {
                // Bırakma hedefi veri alamaz; Explorer işlemi sessizce iptal eder.
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private void OnApplyExternalEdits(object sender, RoutedEventArgs e) => ViewModel.ApplyExternalEdits();

    private void OnDismissExternalEdits(InfoBar sender, object args) => ViewModel.DismissExternalEdits();

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanEdit || !e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(InternalDragFormat))
            return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Arşive ekle";
        e.Handled = true;
    }

    private async void OnListDrop(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanEdit || !e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(InternalDragFormat))
            return;
        // Pencerenin "arşivi aç" bırakma davranışına geçmesin.
        e.Handled = true;
        var deferral = e.GetDeferral();
        List<string> paths;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths = [.. items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p))];
        }
        finally
        {
            deferral.Complete();
        }
        if (paths.Count > 0)
            await AddWithConfirmationAsync(paths);
    }

    // --- Yerleşim: ağaç, önizleme yeri ve boyutları, satır yüksekliği -----------------------------

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArchiveTabViewModel.IsPreviewVisible))
            ApplyLayout();
    }

    private void OnSettingsChanged()
    {
        var setting = SettingsStore.Current.Preview;
        if (setting != _previewSetting)
        {
            // Ayarlardan "Gizli" seçildiyse açık sekmelerde de kapanır; gizliden alt/sağa geçince açılır.
            if (setting == PreviewPlacement.Hidden || _previewSetting == PreviewPlacement.Hidden)
                ViewModel.IsPreviewVisible = setting != PreviewPlacement.Hidden;
            _previewSetting = setting;
        }
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        if (_closed)
            return;
        var settings = SettingsStore.Current;

        var tree = settings.ShowFolderTree;
        TreeColumn.Width = new GridLength(tree ? Math.Clamp(settings.TreeWidth, MinTreeWidth, MaxTreeWidth) : 0);
        FolderTree.Visibility = TreeSplitter.Visibility = tree ? Visibility.Visible : Visibility.Collapsed;

        var rowStyle = (Style)Application.Current.Resources[settings.CompactRows ? "CompactRowStyle" : "ComfortRowStyle"];
        if (!ReferenceEquals(EntriesList.ItemContainerStyle, rowStyle))
            EntriesList.ItemContainerStyle = rowStyle;

        // Önizleme "Gizli" iken düğmeyle açılırsa altta açılır.
        _previewRight = settings.Preview == PreviewPlacement.Right;
        var visible = ViewModel.IsPreviewVisible;
        PreviewSplitter.Visibility = PreviewHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitter.Vertical = !_previewRight;

        ContentArea.RowDefinitions.Clear();
        ContentArea.ColumnDefinitions.Clear();
        var size = new GridLength(visible ? (_previewRight ? settings.PreviewWidth : settings.PreviewHeight) : 0);
        if (_previewRight)
        {
            ContentArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ContentArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var column = new ColumnDefinition { Width = size };
            ContentArea.ColumnDefinitions.Add(column);
            _previewDefinition = column;
        }
        else
        {
            ContentArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            ContentArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = new RowDefinition { Height = size };
            ContentArea.RowDefinitions.Add(row);
            _previewDefinition = row;
        }
        Place(ListPane, 0);
        Place(PreviewSplitter, 1);
        Place(PreviewHost, 2);
        ClampPreview();

        void Place(FrameworkElement element, int index)
        {
            Grid.SetRow(element, _previewRight ? 0 : index);
            Grid.SetColumn(element, _previewRight ? index : 0);
        }
    }

    private double PreviewSize
    {
        get => _previewDefinition switch
        {
            RowDefinition row => row.Height.Value,
            ColumnDefinition column => column.Width.Value,
            _ => 0,
        };
        set
        {
            switch (_previewDefinition)
            {
                case RowDefinition row:
                    row.Height = new GridLength(value);
                    break;
                case ColumnDefinition column:
                    column.Width = new GridLength(value);
                    break;
            }
        }
    }

    private void OnPreviewSplitterDragged(double delta) => PreviewSize = ClampPreviewSize(PreviewSize - delta);

    private void OnPreviewSplitterCompleted()
    {
        var size = PreviewSize;
        if (_previewRight)
            SettingsStore.Update(x => x.PreviewWidth = size, notify: false);
        else
            SettingsStore.Update(x => x.PreviewHeight = size, notify: false);
    }

    private void ClampPreview()
    {
        if (ViewModel.IsPreviewVisible && PreviewSize > 0)
            PreviewSize = ClampPreviewSize(PreviewSize);
    }

    /// <summary>Liste için en az 360 px genişlik / 120 px yükseklik bırakır.</summary>
    private double ClampPreviewSize(double size)
    {
        var (min, available, minList) = _previewRight
            ? (MinPreviewWidth, ContentArea.ActualWidth, MinListWidth)
            : (MinPreviewHeight, ContentArea.ActualHeight, MinListHeight);
        if (available <= 0)
            return Math.Max(min, size); // henüz yerleşmedi
        return Math.Clamp(size, min, Math.Max(min, available - minList - 6));
    }

    private void OnTreeSplitterDragged(double delta) =>
        TreeColumn.Width = new GridLength(Math.Clamp(TreeColumn.Width.Value + delta, MinTreeWidth, MaxTreeWidth));

    private void OnViewMenuOpening(object? sender, object e)
    {
        var settings = SettingsStore.Current;
        PreviewBottomItem.IsChecked = settings.Preview != PreviewPlacement.Right;
        PreviewRightItem.IsChecked = settings.Preview == PreviewPlacement.Right;
        TreeMenuItem.IsChecked = settings.ShowFolderTree;
        CompactMenuItem.IsChecked = settings.CompactRows;
    }

    private void OnPreviewBottom(object sender, RoutedEventArgs e) => PlacePreview(PreviewPlacement.Bottom);

    private void OnPreviewRight(object sender, RoutedEventArgs e) => PlacePreview(PreviewPlacement.Right);

    private void PlacePreview(PreviewPlacement placement)
    {
        ViewModel.IsPreviewVisible = true;
        SettingsStore.Update(x => x.Preview = placement);
    }

    private void OnToggleTree(object sender, RoutedEventArgs e) => SettingsStore.Update(x => x.ShowFolderTree = TreeMenuItem.IsChecked);

    private void OnToggleCompact(object sender, RoutedEventArgs e) => SettingsStore.Update(x => x.CompactRows = CompactMenuItem.IsChecked);

    // --- Klavye kısayolları ----------------------------------------------------------------------

    private void OnFocusSearch(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.SuggestionChosen)
            ViewModel.SearchText = sender.Text;
    }

    private void OnExtractHereAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.ExtractHere(ViewModel.HasSelection ? ExtractScope.Selection : ExtractScope.All);
    }

    private void OnTogglePreviewAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.IsPreviewVisible = !ViewModel.IsPreviewVisible;
    }

    private async void OnUpAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ViewModel.GoUpAsync();
    }
}

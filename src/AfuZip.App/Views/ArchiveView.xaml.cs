using AfuZip.App.ViewModels;
using AfuZip.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace AfuZip.App.Views;

/// <summary>Bir sekmenin içeriği. Görünüm modeli kurucuda verilir; tüm x:Bind'ler ona bağlanır.</summary>
public sealed partial class ArchiveView : UserControl
{
    private const double MinPreviewWidth = 260;
    private const double MinListWidth = 360;

    // Önizleme genişliği sekmeler arasında ortak: yeni sekme son kullanılan genişlikle açılır.
    private static double s_previewWidth = 440;

    private readonly Func<nint> _windowHandle;

    public ArchiveView(ArchiveTabViewModel viewModel, Func<nint> windowHandle)
    {
        ViewModel = viewModel;
        _windowHandle = windowHandle;
        InitializeComponent();

        PreviewHost.Child = _preview = new PreviewPane(viewModel.Preview);
        PreviewHost.Width = s_previewWidth;
        PreviewSplitter.Dragged += OnPreviewSplitterDragged;
        // Pencere küçülünce liste sütunu sıfıra inmesin.
        SizeChanged += (_, _) => ClampPreviewWidth(PreviewHost.Width);
    }

    private readonly PreviewPane _preview;

    /// <summary>Sekme kapanırken: önizlemenin WebView2 sürecini bırak.</summary>
    public void Close() => _preview.Close();

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

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanEdit || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Arşive ekle";
        e.Handled = true;
    }

    private async void OnListDrop(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanEdit || !e.DataView.Contains(StandardDataFormats.StorageItems))
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

    private void OnPreviewSplitterDragged(double delta) => s_previewWidth = ClampPreviewWidth(PreviewHost.Width - delta);

    /// <summary>Ağaç (240), liste ve üç sütun aralığı (3 × 8) için yer bırakır.</summary>
    private double ClampPreviewWidth(double width)
    {
        var max = Math.Max(MinPreviewWidth, ActualWidth - 240 - MinListWidth - 24);
        return PreviewHost.Width = Math.Clamp(width, MinPreviewWidth, max);
    }

    // --- Klavye kısayolları ----------------------------------------------------------------------

    private void OnFocusSearch(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
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

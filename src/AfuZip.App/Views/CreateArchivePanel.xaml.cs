using AfuZip.App.ViewModels;
using AfuZip.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AfuZip.App.Views;

/// <summary>"Arşiv oluştur" diyaloğunun içeriği. Diyaloğun kendisi <see cref="ShowAsync"/> ile kurulur.</summary>
public sealed partial class CreateArchivePanel : UserControl
{
    private readonly Func<nint> _windowHandle;

    // Kaydetme seçicisi seçilen yolda boş bir dosya oluşturur; vazgeçilirse silinir.
    private string? _placeholder;

    public CreateArchivePanel(CreateArchiveViewModel viewModel, Func<nint> windowHandle)
    {
        ViewModel = viewModel;
        _windowHandle = windowHandle;
        InitializeComponent();
    }

    public CreateArchiveViewModel ViewModel { get; }

    /// <returns>Kullanıcı "Oluştur" dediyse istek; vazgeçtiyse null.</returns>
    public static async Task<CreateRequest?> ShowAsync(XamlRoot root, Func<nint> windowHandle, CreateArchiveViewModel viewModel)
    {
        var panel = new CreateArchivePanel(viewModel, windowHandle);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Arşiv oluştur",
            Content = panel,
            PrimaryButtonText = "Oluştur",
            CloseButtonText = "İptal",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = viewModel.IsValid,
        };
        // Varsayılan 548 px preset kartlarına dar geliyor.
        dialog.Resources["ContentDialogMaxWidth"] = 760.0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CreateArchiveViewModel.IsValid))
                dialog.IsPrimaryButtonEnabled = viewModel.IsValid;
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            return viewModel.BuildRequest();

        panel.DeletePlaceholder();
        return null;
    }

    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
        picker.FileTypeFilter.Add("*");
        foreach (var file in await picker.PickMultipleFilesAsync())
            AddSource(file.Path);
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
        picker.FileTypeFilter.Add("*");
        if (await picker.PickSingleFolderAsync() is { } folder)
            AddSource(folder.Path);
    }

    private void OnRemoveSources(object sender, RoutedEventArgs e)
    {
        foreach (var path in SourceList.SelectedItems.OfType<string>().ToList())
            ViewModel.Sources.Remove(path);
    }

    private async void OnBrowseTarget(object sender, RoutedEventArgs e)
    {
        var format = ViewModel.Format;
        var picker = new FileSavePicker
        {
            SuggestedFileName = Path.GetFileName(CreateArchiveViewModel.ReplaceExtension(ViewModel.TargetPath, "")) is { Length: > 0 } name
                ? name
                : "Arşiv",
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
        picker.FileTypeChoices.Add($"{format.Label} arşivi", [format.Extension]);
        if (await picker.PickSaveFileAsync() is { } file)
        {
            // Seçici dosyayı boş olarak oluşturur; asıl arşiv geçici adla yazılıp üzerine taşınır.
            DeletePlaceholder();
            if (new FileInfo(file.Path) is { Exists: true, Length: 0 })
                _placeholder = file.Path;
            ViewModel.TargetPath = file.Path;
        }
    }

    private void DeletePlaceholder()
    {
        try
        {
            if (_placeholder is not null && new FileInfo(_placeholder) is { Exists: true, Length: 0 })
                File.Delete(_placeholder);
        }
        catch (IOException)
        {
        }
        _placeholder = null;
    }

    private void AddSource(string path)
    {
        if (!ViewModel.Sources.Contains(path, StringComparer.OrdinalIgnoreCase))
            ViewModel.Sources.Add(path);
    }
}

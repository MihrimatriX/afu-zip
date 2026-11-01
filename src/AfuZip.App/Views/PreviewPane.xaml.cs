using AfuZip.App.Preview;
using AfuZip.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AfuZip.App.Views;

/// <summary>
/// Önizleme paneli. Yüzeyleri görünüm modeline bağlar; WebView2'yi yalnızca ilk web önizlemesinde oluşturur
/// (WebView2 süreci ~150 ms ve onlarca MB; resim veya hex için gerekmez).
/// </summary>
public sealed partial class PreviewPane : UserControl
{
    private PreviewWebHost? _web;
    private object? _lastWebRequest;
    private bool _fitted;
    private bool _subscribed;

    public PreviewPane(PreviewViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (!_subscribed)
            {
                ViewModel.WebRequested += OnWebRequested;
                _subscribed = true;
            }
            // Sekmeye geri dönüldü ve WebView2 bu arada kapandıysa son önizlemeyi yeniden göster.
            if (_web is { IsAlive: false } && ViewModel.IsWebVisible && _lastWebRequest is { } last)
                OnWebRequested(last);
        };
        // WinUI, denetim yeniden eklendikten sonra da Unloaded gönderebilir; gerçekten ağaçta değilse çık.
        Unloaded += (_, _) =>
        {
            if (!IsLoaded && _subscribed)
            {
                ViewModel.WebRequested -= OnWebRequested;
                _subscribed = false;
            }
        };
        ActualThemeChanged += (_, _) => _web?.SetTheme(ActualTheme == ElementTheme.Dark);
        // Ayraç sürüklenince, pencere büyüyünce ya da panel ilk kez görünür olunca sığdırma yeniden hesaplanır.
        ImageScroller.SizeChanged += (_, _) =>
        {
            if (_fitted)
                ApplyImageSize();
        };
    }

    /// <summary>Sekme kapanırken çağrılır: WebView2 sürecini bırakır.</summary>
    public void Close()
    {
        if (_subscribed)
        {
            ViewModel.WebRequested -= OnWebRequested;
            _subscribed = false;
        }
        _web?.Close();
        _web = null;
    }

    public PreviewViewModel ViewModel { get; }

    private async void OnWebRequested(object request)
    {
        _lastWebRequest = request;
        try
        {
            var web = EnsureWebHost();
            switch (request)
            {
                case ShowMessage message:
                    await web.ShowTextAsync(message);
                    break;
                case PdfRequest pdf:
                    await web.ShowPdfAsync(pdf.Url, pdf.Id);
                    break;
            }
        }
        catch (Exception ex)
        {
            // WebView2 çalışma zamanı yoksa veya başlatılamadıysa önizleme bilgi kartına düşer
            // (yalnızca hâlâ gösterilmesi gereken istek içinse).
            ViewModel.ReportWebFailure(request, ex.Message);
        }
    }

    private PreviewWebHost EnsureWebHost()
    {
        if (_web is not null)
            return _web;

        var view = new WebView2();
        WebHost.Child = view;
        _web = new PreviewWebHost(view, ViewModel);
        _web.Shown += ViewModel.OnWebShown;
        _web.OpenEntryRequested += path => _ = ViewModel.ShowPathAsync(path);
        _web.SetTheme(ActualTheme == ElementTheme.Dark);
        return _web;
    }

    private async void OnLoadAnyway(object sender, RoutedEventArgs e) => await ViewModel.LoadAnywayAsync();

    // --- Resim: açılışta sığdır, çift tıkla sığdır ↔ %100 ------------------------------------------

    // Sığdırma ScrollViewer yakınlaştırmasıyla değil resmin boyutuyla yapılır: ChangeView, içerik ölçülmeden
    // çağrılınca (resim yeni açıldı, panel henüz görünmüyor) sessizce yok sayılıyor ve resim %100'de kalıyordu.
    private void OnImageOpened(object sender, RoutedEventArgs e)
    {
        _fitted = true;
        ApplyImageSize();
    }

    private void OnImageDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _fitted = !_fitted;
        ApplyImageSize();
    }

    private void ApplyImageSize()
    {
        if (PreviewImage.Source is not BitmapImage { PixelWidth: > 0, PixelHeight: > 0 } bitmap)
            return;

        if (_fitted)
        {
            var width = ImageScroller.ActualWidth;
            var height = ImageScroller.ActualHeight;
            if (width <= 0 || height <= 0)
                return; // panel görünür olunca SizeChanged yeniden çağırır
            // Küçük resimler büyütülmez (bulanıklaşır); yalnızca panele sığmayanlar küçültülür.
            var scale = Math.Min(1.0, Math.Min((width - 2) / bitmap.PixelWidth, (height - 2) / bitmap.PixelHeight));
            PreviewImage.Stretch = Stretch.Uniform;
            PreviewImage.Width = Math.Max(1, bitmap.PixelWidth * scale);
            PreviewImage.Height = Math.Max(1, bitmap.PixelHeight * scale);
        }
        else
        {
            PreviewImage.Stretch = Stretch.None;
            PreviewImage.Width = double.NaN;
            PreviewImage.Height = double.NaN;
        }
        ImageScroller.ChangeView(0, 0, 1f, disableAnimation: true);
    }
}

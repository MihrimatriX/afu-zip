using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace AfuZip.App.Preview;

/// <summary>
/// Önizleme panelindeki WebView2. Uygulamanın web katmanı (Monaco, Markdown, JSON/XML ağacı) yerel klasörden
/// <c>https://app.afuzip.preview/</c> sanal host'u ile açılır; arşiv içeriği (markdown resimleri, PDF) diske
/// yazılmadan <c>https://content.afuzip.preview/</c> isteklerine önbellekten yanıt verilir.
///
/// Güvenlik: sayfa yalnızca bu iki host'ta gezinebilir, yeni pencere açamaz, dış bağlantılar sistem
/// tarayıcısında açılır. Arşivden gelen HTML hiçbir zaman HTML olarak çalıştırılmaz (kod olarak gösterilir).
/// </summary>
public sealed class PreviewWebHost
{
    public const string AppHost = "app.afuzip.preview";
    public const string ContentHost = "content.afuzip.preview";
    public static readonly string ContentRoot = $"https://{ContentHost}/";

    private static Task<CoreWebView2Environment>? s_environment;

    private readonly WebView2 _view;
    private readonly IPreviewContentProvider _content;
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _initialization;
    private Task? _pageLoad;
    private ulong _pageNavigationId;
    private CoreWebView2? _subscribed;
    private bool _pageLoaded;
    private int _latestRequest;
    private string _theme = "light";

    public PreviewWebHost(WebView2 view, IPreviewContentProvider content)
    {
        _view = view;
        _content = content;
    }

    /// <summary>Sayfa bir önizlemeyi çizdi: kimlik ve süre (ms).</summary>
    public event Action<int, int>? Shown;

    /// <summary>Markdown'daki arşiv içi bağlantı tıklandı (arşivdeki yol).</summary>
    public event Action<string>? OpenEntryRequested;

    /// <summary>Uygulamanın web katmanının bulunduğu klasör (derlemede AfuZip.Preview.Web/dist buraya kopyalanır).</summary>
    public static string WebRoot => Path.Combine(AppContext.BaseDirectory, "PreviewWeb");

    public static bool IsWebLayerAvailable => File.Exists(Path.Combine(WebRoot, "index.html"));

    /// <summary>
    /// Sekme değişince denetim görsel ağaçtan çıkar; WebView2 bu sırada kapanabilir. Kapandıysa bir sonraki
    /// önizlemede yeniden kurulur.
    /// </summary>
    public bool IsAlive => _view.CoreWebView2 is not null;

    public Task EnsureAsync()
    {
        if (_initialization is { IsCompleted: true } && (_initialization.IsFaulted || !IsAlive))
        {
            _initialization = null;
            _pageLoad = null;
            _pageLoaded = false;
        }
        return _initialization ??= InitializeAsync();
    }

    /// <summary>Metin önizlemesi; sayfa yüklenirken daha yenisi istendiyse bu ileti gönderilmez.</summary>
    public async Task ShowTextAsync(ShowMessage message)
    {
        _latestRequest = message.Id;
        await EnsureAsync();
        await EnsurePageAsync();
        // Sayfa yüklenirken kullanıcı başka dosyaya geçtiyse eski içerik gösterilmesin.
        if (message.Id == _latestRequest)
            Post(JsonSerializer.Serialize(message, WebPreviewJson.Relaxed.ShowMessage));
    }

    /// <summary>PDF, WebView2'nin yerleşik görüntüleyicisiyle üst düzey sayfa olarak açılır.</summary>
    public async Task ShowPdfAsync(Uri url, int id)
    {
        _latestRequest = id;
        await EnsureAsync();
        if (id != _latestRequest)
            return;
        // Uygulama sayfası bekleyen bir yükleme varsa bu gezinme onu iptal eder; NavigationCompleted onu sonlandırır.
        _pageLoaded = false;
        _pageLoad = null;
        _view.CoreWebView2.Navigate(url.AbsoluteUri);
    }

    public void SetTheme(bool dark)
    {
        _theme = dark ? "dark" : "light";
        _view.DefaultBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20)
            : Windows.UI.Color.FromArgb(255, 0xFB, 0xFB, 0xFB);
        if (_pageLoaded && _view.CoreWebView2 is not null)
            Post(JsonSerializer.Serialize(new ThemeMessage(_theme), WebPreviewJson.Relaxed.ThemeMessage));
    }

    /// <summary>Sekme kapanırken: olaylardan çık ve tarayıcı sürecini bırak (aksi halde uygulama kapanana dek yaşar).</summary>
    public void Close()
    {
        if (_subscribed is { } core)
            Unsubscribe(core);
        _ready.TrySetCanceled();
        _view.Close();
    }

    // --- Kurulum ------------------------------------------------------------------------------------

    private async Task InitializeAsync()
    {
        // Paketlenmemiş uygulamada varsayılan veri klasörü exe'nin yanıdır (Program Files'ta yazılamaz).
        if (s_environment is { IsFaulted: true } or { IsCanceled: true })
            s_environment = null;
        s_environment ??= CoreWebView2Environment.CreateWithOptionsAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AfuZip", "WebView2"),
            options: new CoreWebView2EnvironmentOptions()).AsTask();

        await _view.EnsureCoreWebView2Async(await s_environment);
        var core = _view.CoreWebView2;

        // Aynı CoreWebView2 üzerinde yeniden denemede olaylara ikinci kez abone olunmasın (iki tarayıcı sekmesi açılırdı).
        if (!ReferenceEquals(_subscribed, core))
        {
            var settings = core.Settings;
#if DEBUG
            settings.AreDevToolsEnabled = true;
#else
            settings.AreDevToolsEnabled = false;
#endif
            settings.AreBrowserAcceleratorKeysEnabled = false; // Ctrl+F, Ctrl+P vb. Monaco'ya veya uygulamaya kalsın
            settings.IsStatusBarEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsWebMessageEnabled = true;
            settings.AreHostObjectsAllowed = false;

            core.SetVirtualHostNameToFolderMapping(AppHost, WebRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            core.AddWebResourceRequestedFilter($"{ContentRoot}*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.ContextMenuRequested += OnContextMenuRequested;
            core.ProcessFailed += OnProcessFailed;
            _subscribed = core;
        }

        await EnsurePageAsync();
    }

    private void Unsubscribe(CoreWebView2 core)
    {
        core.WebResourceRequested -= OnWebResourceRequested;
        core.WebMessageReceived -= OnWebMessageReceived;
        core.NavigationStarting -= OnNavigationStarting;
        core.NavigationCompleted -= OnNavigationCompleted;
        core.NewWindowRequested -= OnNewWindowRequested;
        core.ContextMenuRequested -= OnContextMenuRequested;
        core.ProcessFailed -= OnProcessFailed;
        _subscribed = null;
    }

    /// <summary>Aynı anda birden çok istek sayfayı beklerse tek yükleme paylaşılır (ikinci gezinme birincisini iptal ederdi).</summary>
    private Task EnsurePageAsync()
    {
        if (_pageLoaded)
            return Task.CompletedTask;
        if (_pageLoad is { IsFaulted: true } or { IsCanceled: true })
            _pageLoad = null;
        return _pageLoad ??= LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        var ready = _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _view.CoreWebView2.Navigate($"https://{AppHost}/index.html");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        _pageLoaded = true;
        Post(JsonSerializer.Serialize(new ThemeMessage(_theme), WebPreviewJson.Relaxed.ThemeMessage));
    }

    private void Post(string json) => _view.CoreWebView2.PostWebMessageAsJson(json);

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        // Çizim süreci çöktüyse bir sonraki önizlemede sayfa yeniden yüklenir.
        _pageLoaded = false;
        _pageLoad = null;
        _ready.TrySetException(new IOException("Önizleme süreci beklenmedik şekilde kapandı."));
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        // Uygulama sayfası yüklenemediyse (veya PDF'e geçildiği için iptal edildiyse) bekleyen istek 20 sn beklemesin.
        if (args.NavigationId == _pageNavigationId && !args.IsSuccess)
            _ready.TrySetException(new IOException($"Önizleme sayfası yüklenemedi ({args.WebErrorStatus})."));
    }

    // --- Olaylar ------------------------------------------------------------------------------------

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        // Yalnızca kendi sayfamızdan gelen mesajlar.
        if (!args.Source.StartsWith($"https://{AppHost}/", StringComparison.OrdinalIgnoreCase))
            return;

        PageMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(args.WebMessageAsJson, WebPreviewJson.Relaxed.PageMessage);
        }
        catch (JsonException)
        {
            return;
        }

        switch (message)
        {
            case { Type: "ready" }:
                _ready.TrySetResult();
                break;
            case { Type: "shown", Id: { } id, Ms: { } ms }:
                Debug.WriteLine($"[önizleme] #{id} sayfada {ms} ms");
                Shown?.Invoke(id, ms);
                break;
            case { Type: "openEntry", Path: { Length: > 0 } path }:
                OpenEntryRequested?.Invoke(path);
                break;
            case { Type: "openExternal", Url: { } url }:
                OpenExternal(url);
                break;
            case { Type: "error", Message: { } error }:
                Debug.WriteLine($"[önizleme] sayfa hatası: {error}");
                break;
        }
    }

    private async void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var content = await _content.OpenContentAsync(new Uri(args.Request.Uri));
            args.Response = content is null
                ? sender.Environment.CreateWebResourceResponse(null, 404, "Not Found", "")
                : sender.Environment.CreateWebResourceResponse(
                    content.Stream.AsRandomAccessStream(), 200, "OK",
                    $"Content-Type: {content.MimeType}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[önizleme] içerik sunulamadı: {ex.Message}");
            args.Response = sender.Environment.CreateWebResourceResponse(null, 500, "Error", "");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri))
        {
            args.Cancel = true;
            return;
        }

        var allowed = uri.Scheme == Uri.UriSchemeHttps &&
                      (uri.Host == AppHost || (uri.Host == ContentHost && uri.AbsolutePath.StartsWith("/pdf/", StringComparison.Ordinal)));
        if (allowed)
        {
            if (uri.Host == AppHost)
                _pageNavigationId = args.NavigationId;
            return;
        }

        args.Cancel = true;
        // PDF'teki bir bağlantıya kullanıcı tıkladıysa sistem tarayıcısında aç.
        if (args.IsUserInitiated)
            OpenExternal(args.Uri);
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (args.IsUserInitiated)
            OpenExternal(args.Uri);
    }

    /// <summary>Sağ tık menüsünde yalnızca kopyalama ve seçme kalsın (geri, yenile, incele yok).</summary>
    private static void OnContextMenuRequested(CoreWebView2 sender, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        string[] keep = ["copy", "selectAll", "copyImage", "copyLinkLocation"];
        var items = args.MenuItems;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Kind == CoreWebView2ContextMenuItemKind.Separator || !keep.Contains(items[i].Name))
                items.RemoveAt(i);
        }
    }

    private static void OpenExternal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto))
            _ = Launcher.LaunchUriAsync(uri);
    }
}

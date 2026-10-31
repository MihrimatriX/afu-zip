using System.Diagnostics;
using System.Text;
using AfuZip.App.Preview;
using AfuZip.Core;
using AfuZip.Core.Preview;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace AfuZip.App.ViewModels;

public enum PreviewSurface
{
    /// <summary>Seçim yok.</summary>
    None,

    /// <summary>Bilgi kartı (klasör, iç arşiv, büyük/şifreli dosya, hata).</summary>
    Info,

    /// <summary>Web katmanı bulunamadığında düz metin yedeği.</summary>
    Text,
    Image,
    Web,
    Media,
    Hex,
}

public sealed record EncodingChoice(string Label, Encoding? Encoding)
{
    public override string ToString() => Label;
}

/// <summary>
/// Bir sekmenin önizleme paneli. Seçilen girdiyi önbellekten (yoksa motordan) açar, türünü içerik imzasından
/// belirler ve uygun yüzeye verir: resim, WebView (kod, Markdown, JSON/XML, SVG, PDF), medya oynatıcı veya hex.
/// Her yeni seçim öncekini iptal eder; geç gelen sonuçlar nesil sayacıyla ayıklanır.
/// </summary>
public sealed class PreviewViewModel(PreviewCache cache) : ObservableObject, IPreviewContentProvider
{
    /// <summary>Bundan büyük girdiler kullanıcı istemeden açılmaz (çıkarma süresi ve geçici disk).</summary>
    private const long AutoPreviewLimit = 64L << 20;

    /// <summary>Hex görünümünde gösterilen en fazla bayt.</summary>
    private const long HexLimit = 256L << 20;

    private static readonly IReadOnlyDictionary<string, string> LanguageNames = new Dictionary<string, string>
    {
        ["csharp"] = "C#", ["typescript"] = "TypeScript", ["javascript"] = "JavaScript", ["cpp"] = "C/C++",
        ["python"] = "Python", ["json"] = "JSON", ["xml"] = "XML", ["html"] = "HTML", ["css"] = "CSS",
        ["scss"] = "SCSS", ["markdown"] = "Markdown", ["yaml"] = "YAML", ["ini"] = "INI", ["shell"] = "Shell",
        ["powershell"] = "PowerShell", ["bat"] = "Batch", ["sql"] = "SQL", ["rust"] = "Rust", ["go"] = "Go",
        ["java"] = "Java", ["kotlin"] = "Kotlin", ["lua"] = "Lua", ["plaintext"] = "Metin", ["dockerfile"] = "Dockerfile",
        ["razor"] = "Razor", ["fsharp"] = "F#", ["vb"] = "Visual Basic", ["php"] = "PHP", ["ruby"] = "Ruby",
        ["swift"] = "Swift", ["dart"] = "Dart", ["wgsl"] = "WGSL", ["graphql"] = "GraphQL", ["hcl"] = "HCL",
    };

    private CancellationTokenSource? _cts;
    private int _generation;
    private int _webId;
    private IArchiveReader? _reader;
    private ArchiveEntry? _entry;
    private PreviewSource? _source;
    private PreviewType? _type;
    private Dictionary<string, ArchiveEntry>? _pathIndex;
    private int _loadedGeneration;
    private long _startedAt;
    private Stream? _mediaStream;
    private CancellationTokenSource _contentCts = new();

    // Parolası bu oturumda girilmiş okuyucular: şifreli girdiler seçilince parola diyaloğu kendiliğinden açılmasın,
    // ama bir kez açılan arşivde sorun olmasın. İç içe arşivin parolası ayrı olabilir; bu yüzden okuyucu başına.
    private readonly HashSet<IArchiveReader> _unlocked = new(ReferenceEqualityComparer.Instance);

    private PreviewSurface _surface;
    private string _title = "";
    private string _subtitle = "";
    private string _details = "";
    private string _message = "";
    private string _loadingText = "";
    private bool _isLoading;
    private bool _canLoadAnyway;
    private string _loadAnywayText = "";
    private bool _canChooseEncoding;
    private int _encodingIndex;
    private string _plainText = "";
    private ImageSource? _image;
    private HexDocument? _hex;
    private IMediaPlaybackSource? _media;

    /// <summary>WebView'a gidecek içerik: <see cref="ShowMessage"/> veya PDF adresi (<see cref="Uri"/>).</summary>
    public event Action<object>? WebRequested;

    public IReadOnlyList<EncodingChoice> Encodings { get; } =
    [
        new("Otomatik", null),
        .. TextDecoder.Choices.Select(e => new EncodingChoice(EncodingName(e), e)),
    ];

    public PreviewSurface Surface
    {
        get => _surface;
        private set
        {
            if (!SetProperty(ref _surface, value))
                return;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsInfoVisible));
            OnPropertyChanged(nameof(IsTextVisible));
            OnPropertyChanged(nameof(IsImageVisible));
            OnPropertyChanged(nameof(IsWebVisible));
            OnPropertyChanged(nameof(IsMediaVisible));
            OnPropertyChanged(nameof(IsHexVisible));
            OnPropertyChanged(nameof(HasHeader));
            OnPropertyChanged(nameof(HasBanner));
        }
    }

    public bool IsEmpty => _surface == PreviewSurface.None;
    public bool HasHeader => _surface != PreviewSurface.None;
    public bool IsInfoVisible => _surface == PreviewSurface.Info;
    public bool IsTextVisible => _surface == PreviewSurface.Text;
    public bool IsImageVisible => _surface == PreviewSurface.Image;
    public bool IsWebVisible => _surface == PreviewSurface.Web;
    public bool IsMediaVisible => _surface == PreviewSurface.Media;
    public bool IsHexVisible => _surface == PreviewSurface.Hex;

    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }
    public string Details { get => _details; private set => SetProperty(ref _details, value); }
    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
                OnPropertyChanged(nameof(HasBanner));
        }
    }

    /// <summary>Bilgi kartı dışındaki yüzeylerde (hex, resim) mesaj başlığın altında şerit olarak gösterilir.</summary>
    public bool HasBanner => _message.Length > 0 && _surface is not (PreviewSurface.Info or PreviewSurface.None);
    public string LoadingText { get => _loadingText; private set => SetProperty(ref _loadingText, value); }
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool CanLoadAnyway { get => _canLoadAnyway; private set => SetProperty(ref _canLoadAnyway, value); }
    public string LoadAnywayText { get => _loadAnywayText; private set => SetProperty(ref _loadAnywayText, value); }
    public bool CanChooseEncoding { get => _canChooseEncoding; private set => SetProperty(ref _canChooseEncoding, value); }
    public string PlainText { get => _plainText; private set => SetProperty(ref _plainText, value); }
    public ImageSource? Image { get => _image; private set => SetProperty(ref _image, value); }
    public HexDocument? Hex { get => _hex; private set => SetProperty(ref _hex, value); }
    public IMediaPlaybackSource? Media { get => _media; private set => SetProperty(ref _media, value); }

    /// <summary>0 = otomatik. Değişince aynı içerik yeniden çözülür (yeniden çıkarma yok).</summary>
    public int EncodingIndex
    {
        get => _encodingIndex;
        set
        {
            if (value < 0 || !SetProperty(ref _encodingIndex, value))
                return;
            if (_source is not null && _type is { IsTextual: true } type && _entry is not null)
                _ = RedecodeAsync(_source, type, _entry);
        }
    }

    // --- Seçim ---------------------------------------------------------------------------------------

    public void Clear()
    {
        Restart(keepSource: false);
        _reader = null;
        _entry = null;
        Title = "";
        Subtitle = "";
        Details = "";
        Message = "";
        Surface = PreviewSurface.None;
    }

    public void ShowFolder(string name, FolderStats stats)
    {
        Restart(keepSource: false);
        _entry = null;
        Title = name;
        Subtitle = "Klasör";
        Details = $"{stats.Files:N0} dosya\n{ByteSize.Format(stats.Size)}" +
                  (stats.PackedSize > 0 ? $" (sıkıştırılmış {ByteSize.Format(stats.PackedSize)})" : "");
        Message = "";
        Surface = PreviewSurface.Info;
    }

    /// <param name="force">Büyük veya şifreli girdi için kullanıcı "önizle" dedi.</param>
    public async Task ShowAsync(IArchiveReader reader, ArchiveEntry entry, bool force = false)
    {
        var ct = Restart(keepSource: false);
        var generation = _generation;
        _startedAt = Stopwatch.GetTimestamp();

        // Elle seçilen kodlama yalnızca o dosya için geçerli; sonraki dosya yine otomatik tespit edilir.
        if (_encodingIndex != 0)
        {
            _encodingIndex = 0;
            OnPropertyChanged(nameof(EncodingIndex));
        }

        if (!ReferenceEquals(_reader, reader))
            _pathIndex = null;
        _reader = reader;
        _entry = entry;
        Title = entry.Name;
        Subtitle = ByteSize.Format(entry.Size);
        Details = Describe(entry);
        Message = "";
        Surface = PreviewSurface.Info;

        if (entry.IsSymlink)
        {
            Message = "Sembolik bağlantı; önizlenmez.";
            return;
        }
        if (NestedArchive.LooksLikeArchive(entry))
        {
            Subtitle = $"Arşiv · {ByteSize.Format(entry.Size)}";
            Message = "İç arşiv. Açmak için çift tıklayın veya Enter'a basın.";
            return;
        }

        var cached = cache.Contains(reader, entry.Index);
        if (!force && !cached)
        {
            if (entry.IsEncrypted && !_unlocked.Contains(reader))
            {
                Offer("Bu dosya şifreli.", "Parolayı girip önizle");
                return;
            }
            if (entry.Size > AutoPreviewLimit)
            {
                Offer($"Büyük dosya: önizleme için {ByteSize.Format(entry.Size)} açılacak.", "Önizle");
                return;
            }
        }

        _ = ShowSpinnerLaterAsync(generation, reader.IsSolid && !cached ? "Solid blok açılıyor…" : "Açılıyor…");
        try
        {
            // 7-Zip ilerlemeyi çok sık bildirir; UI thread'i boğulmasın.
            var progress = new ThrottledProgress<ArchiveProgress>(new Progress<ArchiveProgress>(p =>
            {
                if (generation == _generation && p.Fraction is { } fraction)
                    LoadingText = $"{(reader.IsSolid ? "Solid blok açılıyor" : "Açılıyor")}… %{fraction * 100:0}";
            }), TimeSpan.FromMilliseconds(100));

            var source = await cache.OpenAsync(reader, entry, progress, ct);
            if (ct.IsCancellationRequested)
            {
                source.Dispose(); // bu sırada başka dosya seçildi; önbellekte tutulmasın diye bırak
                return;
            }
            if (entry.IsEncrypted)
                _unlocked.Add(reader);

            _source = source;
            var type = _type = PreviewClassifier.Classify(entry.Name, source.ReadHead(PreviewClassifier.HeadSize));
            await RenderAsync(source, type, entry, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ArchivePasswordException ex) when (generation == _generation)
        {
            _unlocked.Remove(reader);
            Offer(ex.WasProvided ? "Parola yanlış." : "Parola girilmedi.", "Parolayı girip önizle");
        }
        catch (Exception ex) when (generation == _generation)
        {
            Surface = PreviewSurface.Info;
            Message = $"Önizlenemedi: {ex.Message}";
        }
        catch (Exception) when (generation != _generation)
        {
            // Bu arada başka bir dosya seçildi; eski önizlemenin hatası (kapanan sekme, atılan önbellek) yok sayılır.
        }
        finally
        {
            _loadedGeneration = generation;
            if (generation == _generation)
                IsLoading = false;
        }
    }

    public Task LoadAnywayAsync() =>
        _reader is { } reader && _entry is { } entry ? ShowAsync(reader, entry, force: true) : Task.CompletedTask;

    /// <summary>Markdown'daki arşiv içi bağlantı: o girdiyi önizle.</summary>
    public Task ShowPathAsync(string path) =>
        _reader is { } reader && FindEntry(path) is { IsDirectory: false } entry ? ShowAsync(reader, entry) : Task.CompletedTask;

    /// <summary>Okuyucu kapanıyor: ona ait önizlemeyi ve önbelleği bırak.</summary>
    public void Release(IArchiveReader reader)
    {
        if (ReferenceEquals(_reader, reader))
        {
            Clear();
            // Markdown resimleri gibi süren içerik istekleri kapanan okuyucuya erişmesin.
            _contentCts.Cancel();
            _contentCts.Dispose();
            _contentCts = new CancellationTokenSource();
        }
        _unlocked.Remove(reader);
        cache.Evict(reader);
    }

    /// <summary>WebView2 başlatılamadı (çalışma zamanı eksik, sayfa yüklenemedi). Eski bir istek içinse yok sayılır.</summary>
    public void ReportWebFailure(object request, string reason)
    {
        var id = request switch
        {
            ShowMessage message => message.Id,
            PdfRequest pdf => pdf.Id,
            _ => -1,
        };
        if (id != _webId || _surface != PreviewSurface.Web)
            return;
        Surface = PreviewSurface.Info;
        Message = $"Önizleme bileşeni (WebView2) başlatılamadı: {reason}";
    }

    /// <summary>WebView sayfası önizlemeyi çizdi.</summary>
    public void OnWebShown(int id, int pageMs)
    {
        if (id != _webId)
            return;
        var total = Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
        Debug.WriteLine($"[önizleme] {Title}: toplam {total:N0} ms (sayfa {pageMs} ms)");
    }

    // --- Çizim ---------------------------------------------------------------------------------------

    private async Task RenderAsync(PreviewSource source, PreviewType type, ArchiveEntry entry, CancellationToken ct)
    {
        switch (type.Kind)
        {
            case PreviewKind.Image:
                await ShowImageAsync(source, type, entry, ct);
                break;

            case PreviewKind.Pdf when PreviewWebHost.IsWebLayerAvailable:
                Subtitle = $"PDF · {ByteSize.Format(source.Length)}";
                Surface = PreviewSurface.Web;
                ++_webId;
                WebRequested?.Invoke(new PdfRequest(_webId, new Uri($"{PreviewWebHost.ContentRoot}pdf/{_webId}/{Uri.EscapeDataString(entry.Name)}")));
                break;

            case PreviewKind.Video or PreviewKind.Audio:
                Subtitle = $"{(type.Kind == PreviewKind.Video ? "Video" : "Ses")} · {ByteSize.Format(source.Length)}";
                _mediaStream = source.OpenRead();
                Media = MediaSource.CreateFromStream(_mediaStream.AsRandomAccessStream(), type.MimeType);
                Surface = PreviewSurface.Media;
                break;

            case PreviewKind.Archive:
                Subtitle = $"Arşiv · {ByteSize.Format(source.Length)}";
                Message = "İçerik bir arşiv. Uzantısı tanınmadığı için klasör gibi açılmıyor; çıkarıp açabilirsiniz.";
                Surface = PreviewSurface.Info;
                break;

            case PreviewKind.Text or PreviewKind.Markdown or PreviewKind.Json or PreviewKind.Xml or PreviewKind.Svg:
                await ShowTextAsync(source, type, entry, ct);
                break;

            default:
                ShowHex(source, "İkili dosya");
                break;
        }
    }

    private async Task ShowImageAsync(PreviewSource source, PreviewType type, ArchiveEntry entry, CancellationToken ct)
    {
        var bitmap = new BitmapImage();
        try
        {
            // BitmapImage veriyi kendi kopyalar; stream'i açık bırakmak (bellek) yeterli.
            await bitmap.SetSourceAsync(source.OpenRead().AsRandomAccessStream());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!ct.IsCancellationRequested)
            {
                ShowHex(source, "Resim");
                Message = "Windows bu resim biçimini açamadı (HEIF/AVIF için Microsoft Store'daki uzantılar gerekebilir).";
            }
            return;
        }

        ct.ThrowIfCancellationRequested();
        Image = bitmap;
        var format = type.MimeType.Replace("image/", "", StringComparison.Ordinal).Replace("x-icon", "ico").ToUpperInvariant();
        Subtitle = $"{format} · {bitmap.PixelWidth}×{bitmap.PixelHeight} · {ByteSize.Format(entry.Size)}";
        Surface = PreviewSurface.Image;
    }

    /// <summary>Kodlama elle değişti: aynı içerik (önbellekten) yeniden çözülür.</summary>
    private async Task RedecodeAsync(PreviewSource source, PreviewType type, ArchiveEntry entry)
    {
        var ct = Restart(keepSource: true);
        var generation = _generation;
        try
        {
            await ShowTextAsync(source, type, entry, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (generation == _generation)
        {
            Surface = PreviewSurface.Info;
            Message = $"Önizlenemedi: {ex.Message}";
        }
        catch (Exception) when (generation != _generation)
        {
        }
    }

    private async Task ShowTextAsync(PreviewSource source, PreviewType type, ArchiveEntry entry, CancellationToken ct)
    {
        var forced = Encodings[Math.Clamp(_encodingIndex, 0, Encodings.Count - 1)].Encoding;
        DecodedText decoded;
        using (var stream = source.OpenRead())
            decoded = await TextDecoder.ReadAsync(stream, forced, TextDecoder.DefaultMaxBytes, ct);
        ct.ThrowIfCancellationRequested();

        var language = type.Language ?? CodeLanguages.PlainText;
        var label = type.Kind switch
        {
            PreviewKind.Svg => "SVG",
            _ => LanguageNames.TryGetValue(language, out var name) ? name : language,
        };
        Subtitle = $"{label} · {ByteSize.Format(source.Length)} · {EncodingName(decoded.Encoding)}{(forced is null ? " (otomatik)" : "")}" +
                   (decoded.Truncated ? $" · ilk {ByteSize.Format(decoded.BytesRead)}" : "");
        CanChooseEncoding = true;

        if (!PreviewWebHost.IsWebLayerAvailable)
        {
            // Web katmanı derlenmemişse (npm yok) düz metin yedeği.
            PlainText = decoded.Text.Length > 200_000 ? decoded.Text[..200_000] + "\n…" : decoded.Text;
            Surface = PreviewSurface.Text;
            return;
        }

        var view = type.Kind switch
        {
            PreviewKind.Markdown => "markdown",
            PreviewKind.Json => "json",
            PreviewKind.Xml => "xml",
            PreviewKind.Svg => "svg",
            _ => "code",
        };
        Surface = PreviewSurface.Web;
        WebRequested?.Invoke(new ShowMessage(++_webId, view, entry.Name, entry.Path, language, decoded.Text, decoded.Truncated,
            BaseUrlFor(entry.Path)));
    }

    private void ShowHex(PreviewSource source, string label)
    {
        var hex = new HexDocument(source.OpenRead(), ownsStream: true, maxBytes: HexLimit);
        Hex = hex;
        Subtitle = $"{label} · {ByteSize.Format(source.Length)}" + (hex.IsTruncated ? $" · ilk {ByteSize.Format(HexLimit)}" : "");
        Surface = PreviewSurface.Hex;
    }

    // --- WebView içerik sunucusu ---------------------------------------------------------------------

    /// <summary>
    /// https://content.afuzip.preview/entry/&lt;yol&gt; → arşivdeki resim (Markdown'daki göreli resimler),
    /// https://content.afuzip.preview/pdf/&lt;kimlik&gt;/&lt;ad&gt; → o anki PDF. Başka türler sunulmaz.
    /// </summary>
    public async Task<PreviewContent?> OpenContentAsync(Uri uri, CancellationToken ct = default)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[0] == "pdf")
        {
            return int.TryParse(segments[1], out var id) && id == _webId && _type?.Kind == PreviewKind.Pdf && _source is { } pdf
                ? new PreviewContent(pdf.OpenRead(), "application/pdf")
                : null;
        }

        if (segments.Length >= 2 && segments[0] == "entry" && _reader is { } reader)
        {
            var path = string.Join('/', segments.Skip(1).Select(Uri.UnescapeDataString));
            if (FindEntry(path) is not { IsDirectory: false, IsEncrypted: false } entry || entry.Size > AutoPreviewLimit)
                return null;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _contentCts.Token);
            // Açık stream önbellek girdiyi atsa da (FileShare.Delete) okunabilir; kaynak hemen bırakılır.
            using var source = await cache.OpenAsync(reader, entry, ct: linked.Token);
            var type = PreviewClassifier.Classify(entry.Name, source.ReadHead(PreviewClassifier.HeadSize));
            return type.Kind is PreviewKind.Image or PreviewKind.Svg ? new PreviewContent(source.OpenRead(), type.MimeType) : null;
        }

        return null;
    }

    private ArchiveEntry? FindEntry(string path)
    {
        if (_reader is null)
            return null;
        if (_pathIndex is null)
        {
            // README'lerde büyük/küçük harf uyuşmazlığı sık; ilk eşleşen kazanır.
            _pathIndex = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _reader.Entries)
                _pathIndex.TryAdd(entry.Path.TrimEnd('/'), entry);
        }
        return _pathIndex.GetValueOrDefault(ArchiveEntry.NormalizePath(path).TrimEnd('/'));
    }

    private static string BaseUrlFor(string entryPath)
    {
        var slash = entryPath.LastIndexOf('/');
        var folder = slash < 0 ? "" : entryPath[..slash];
        var escaped = string.Join('/', folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return $"{PreviewWebHost.ContentRoot}entry/{(escaped.Length > 0 ? escaped + "/" : "")}";
    }

    // --- Yardımcılar ---------------------------------------------------------------------------------

    /// <summary>Süren önizlemeyi iptal eder ve yüzeyleri boşaltır; yeni işlemin iptal belirtecini döndürür.</summary>
    private CancellationToken Restart(bool keepSource)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _generation++;

        if (!keepSource)
        {
            _source?.Dispose(); // önbellekteki girdi artık atılabilir
            _source = null;
            _type = null;
            CanChooseEncoding = false;
        }
        IsLoading = false;
        CanLoadAnyway = false;
        Image = null;
        PlainText = "";

        var media = Media;
        Media = null; // önce oynatıcıdan ayır, sonra bırak
        (media as IDisposable)?.Dispose();
        _mediaStream?.Dispose(); // MediaSource.Dispose .NET stream'ini kapatmaz
        _mediaStream = null;

        var hex = Hex;
        Hex = null;
        hex?.Dispose();

        return _cts.Token;
    }

    private void Offer(string message, string action)
    {
        Surface = PreviewSurface.Info;
        Message = message;
        LoadAnywayText = action;
        CanLoadAnyway = true;
    }

    /// <summary>Kısa süren açılışlarda yanıp sönmesin diye yükleme göstergesi gecikmeli çıkar.</summary>
    private async Task ShowSpinnerLaterAsync(int generation, string text)
    {
        await Task.Delay(150);
        // Açılış 150 ms içinde (başarıyla ya da hatayla) bittiyse gösterge hiç çıkmaz.
        if (generation != _generation || _loadedGeneration == generation)
            return;
        LoadingText = text;
        IsLoading = true;
    }

    private static string Describe(ArchiveEntry entry)
    {
        var lines = new List<string> { $"Boyut: {ByteSize.Format(entry.Size)}" };
        if (entry.PackedSize is { } packed)
            lines.Add($"Sıkıştırılmış: {ByteSize.Format(packed)} ({ByteSize.FormatRatio(entry.Ratio)})");
        if (entry.Method is { } method)
            lines.Add($"Yöntem: {method}");
        if (entry.Modified is { } modified)
            lines.Add($"Değiştirilme: {modified.LocalDateTime:g}");
        if (entry.Crc is { } crc)
            lines.Add($"CRC32: {crc:X8}");
        if (entry.IsEncrypted)
            lines.Add("Şifreli");
        lines.Add($"Yol: {entry.Path}");
        return string.Join('\n', lines);
    }

    private static string EncodingName(Encoding encoding) => encoding.CodePage switch
    {
        65001 => "UTF-8",
        1200 => "UTF-16 LE",
        1201 => "UTF-16 BE",
        12000 => "UTF-32",
        1254 => "Türkçe (Windows-1254)",
        28599 => "Türkçe (ISO-8859-9)",
        857 => "Türkçe DOS (857)",
        1252 => "Batı Avrupa (1252)",
        437 => "DOS (437)",
        var cp => $"Kod sayfası {cp}",
    };
}

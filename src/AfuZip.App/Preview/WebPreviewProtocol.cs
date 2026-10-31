using System.Text.Json.Serialization;

namespace AfuZip.App.Preview;

// AfuZip.Preview.Web/src/protocol.ts ile birebir aynı tutulmalı.

/// <summary>Sayfaya gönderilen metin önizlemesi.</summary>
public sealed record ShowMessage(
    int Id,
    string View,
    string Name,
    string Path,
    string Language,
    string Text,
    bool Truncated,
    string? BaseUrl)
{
    public string Type => "show";
}

/// <summary>PDF önizlemesi: WebView2'nin kendi görüntüleyicisi bu adresi açar.</summary>
public sealed record PdfRequest(int Id, Uri Url);

public sealed record ThemeMessage(string Theme)
{
    public string Type => "theme";
}

public sealed record ClearMessage
{
    public string Type => "clear";
}

/// <summary>Sayfadan gelen mesaj; yalnızca kullanılan alanlar.</summary>
public sealed record PageMessage(string Type, int? Id, int? Ms, string? Url, string? Path, string? Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ShowMessage))]
[JsonSerializable(typeof(ThemeMessage))]
[JsonSerializable(typeof(ClearMessage))]
[JsonSerializable(typeof(PageMessage))]
internal sealed partial class WebPreviewJson : JsonSerializerContext
{
    /// <summary>
    /// Varsayılan kodlayıcı ASCII dışındaki her karakteri \uXXXX yapar: 5 MB Türkçe metin ~6 kat büyür.
    /// İleti HTML'e gömülmüyor, doğrudan JSON olarak ayrıştırılıyor; gevşek kaçış güvenli.
    /// </summary>
    public static WebPreviewJson Relaxed { get; } = new(new System.Text.Json.JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });
}

/// <summary>Önizleme panelinin WebView'dan istediği içerik (markdown resimleri, PDF).</summary>
public sealed record PreviewContent(Stream Stream, string MimeType);

public interface IPreviewContentProvider
{
    /// <returns>null: içerik yok veya bu türün sunulmasına izin verilmiyor (404).</returns>
    Task<PreviewContent?> OpenContentAsync(Uri uri, CancellationToken ct = default);
}

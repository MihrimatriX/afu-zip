namespace AfuZip.Core;

public enum RegistryKind
{
    String,
    DWord,
}

/// <summary>HKEY_CURRENT_USER altındaki bir anahtar ve (isteğe bağlı) bir değeri. <see cref="Name"/> null ise varsayılan değer.</summary>
public sealed record RegistryWrite(string Key, string? Name, object Value, RegistryKind Kind = RegistryKind.String);

/// <summary>Kaldırırken silinecek değer (paylaşılan anahtarlardaki, ör. .7z\OpenWithProgids).</summary>
public sealed record RegistryValueRemoval(string Key, string Name);

/// <summary>
/// Explorer entegrasyonu, yalnızca geçerli kullanıcı için (HKCU, yönetici izni gerekmez): klasik sağ tık menüsü,
/// "Birlikte aç" listesi ve Ayarlar → Varsayılan uygulamalar kaydı. Windows 11'de klasik menü
/// "Daha fazla seçenek göster" altındadır; yeni menü imzalı sparse paketle gelir (ShellIntegration).
/// <para>
/// Explorer eklentisi (AfuZip.ShellExtension.dll) yanındaysa tek bir "AFU Zip" alt menüsü eklentiye yönlenir:
/// başlıklar seçime göre değişir ("proje\" klasörüne çıkar, "Belgeler.7z" olarak sıkıştır) ve çok seçim tek
/// komutla gelir. Eklenti yoksa sabit kayıt defteri menüleri kullanılır.
/// </para>
/// </summary>
public static class ShellRegistration
{
    public const string ProgId = "AfuZip.Archive";
    public const string AppKey = @"Software\AfuZip";
    public const string ExePathValue = "ShellExePath";

    private const string Classes = @"Software\Classes";
    private const string ArchiveMenu = "AfuZip.ArchiveMenu";
    private const string CompressMenu = "AfuZip.CompressMenu";
    private const string ArchiveVerb = "AfuZip";
    private const string CompressVerb = "AfuZip.Compress";
    private const string CapabilitiesKey = AppKey + @"\Capabilities";
    private const string RegisteredApplications = @"Software\RegisteredApplications";
    private const string RegisteredName = "AFU Zip";

    /// <summary>Eklentinin klasik menü sınıfı (yeni menüdeki paketli sınıftan ayrı).</summary>
    public const string HandlerClsid = "{4538D22E-A004-4452-8898-AF47558519CB}";
    private const string HandlerKey = Classes + @"\CLSID\" + HandlerClsid;
    private static readonly string[] HandlerTargets = ["*", "Directory"];

    // ECF_SEPARATORBEFORE: alt menüde öğeden önce ayırıcı çizgi.
    private const int SeparatorBefore = 0x20;

    /// <summary>
    /// "Birlikte aç" ve sağ tık menüsünün ekleneceği uzantılar. Zip tabanlı belge ve paketler (docx, jar, apk)
    /// bilerek yok: onları kendi programları açmalı.
    /// </summary>
    public static IReadOnlyList<string> Extensions { get; } =
    [
        ".7z", ".zip", ".zipx", ".rar", ".cbz", ".cbr", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz",
        ".zst", ".tzst", ".lz4", ".lzma", ".br", ".iso", ".cab", ".wim", ".esd", ".001",
    ];

    /// <param name="handlerDll">Explorer eklentisinin tam yolu; null ise sabit menüler yazılır.</param>
    public static IReadOnlyList<RegistryWrite> Build(string exePath, string? handlerDll = null)
    {
        var exe = Path.GetFileName(exePath);
        var icon = $"{exePath},0";
        string Command(string? flags) => flags is null ? $"\"{exePath}\" \"%1\"" : $"\"{exePath}\" {flags} \"%1\"";

        var writes = new List<RegistryWrite>
        {
            // ProgID: "Birlikte aç" ve varsayılan uygulama seçildiğinde kullanılan tür.
            new($@"{Classes}\{ProgId}", null, "AFU Zip arşivi"),
            new($@"{Classes}\{ProgId}", "FriendlyTypeName", "AFU Zip arşivi"),
            new($@"{Classes}\{ProgId}\DefaultIcon", null, icon),
            new($@"{Classes}\{ProgId}\shell\open", null, "AFU Zip ile aç"),
            new($@"{Classes}\{ProgId}\shell\open\command", null, Command(null)),

            // "Birlikte aç" listesinde uygulamanın adı ve desteklediği türler.
            new($@"{Classes}\Applications\{exe}", "FriendlyAppName", "AFU Zip"),
            new($@"{Classes}\Applications\{exe}\DefaultIcon", null, icon),
            new($@"{Classes}\Applications\{exe}\shell\open\command", null, Command(null)),

            // Ayarlar → Uygulamalar → Varsayılan uygulamalar.
            new(CapabilitiesKey, "ApplicationName", "AFU Zip"),
            new(CapabilitiesKey, "ApplicationDescription", "Sade ve güçlü arşiv programı (7-Zip motoru)"),
            new(CapabilitiesKey, "ApplicationIcon", icon),
            new(RegisteredApplications, RegisteredName, CapabilitiesKey),
            new(AppKey, ExePathValue, exePath),
        };

        foreach (var extension in Extensions)
        {
            writes.Add(new($@"{Classes}\{extension}\OpenWithProgids", ProgId, ""));
            writes.Add(new($@"{Classes}\Applications\{exe}\SupportedTypes", extension, ""));
            writes.Add(new($@"{CapabilitiesKey}\FileAssociations", extension, ProgId));
        }

        if (handlerDll is not null)
        {
            // Klasik menü eklentiye yönlenir: tek "AFU Zip" öğesi, alt komutlar seçime göre kendini gizler.
            writes.Add(new(HandlerKey, null, "AFU Zip menüsü"));
            writes.Add(new($@"{HandlerKey}\InprocServer32", null, handlerDll));
            writes.Add(new($@"{HandlerKey}\InprocServer32", "ThreadingModel", "Apartment"));
            foreach (var target in HandlerTargets)
            {
                var key = $@"{Classes}\{target}\shell\{ArchiveVerb}";
                writes.Add(new(key, "MUIVerb", "AFU Zip"));
                writes.Add(new(key, "Icon", icon));
                writes.Add(new(key, "ExplorerCommandHandler", HandlerClsid));
                // Varsayılan "Document" modeli 15'ten fazla seçimde öğeyi gizler.
                writes.Add(new(key, "MultiSelectModel", "Player"));
            }
            return writes;
        }

        // Eklenti yok: arşivlerin alt menüsü bir kez tanımlanır, her uzantı ExtendedSubCommandsKey ile ona bağlanır.
        AddCommand(writes, ArchiveMenu, "01open", "AFU Zip'te aç", icon, Command(null));
        AddCommand(writes, ArchiveMenu, "02extracthere", "Buraya çıkar", icon, Command("--extract-here"));
        AddCommand(writes, ArchiveMenu, "03extractfolder", "Arşiv adıyla klasöre çıkar", icon, Command("--extract-folder"));
        AddCommand(writes, ArchiveMenu, "04test", "Arşivi test et", icon, Command("--test"), SeparatorBefore);

        AddCommand(writes, CompressMenu, "01add", "Arşive ekle…", icon, Command("--add"));
        AddCommand(writes, CompressMenu, "02sevenzip", "7z olarak sıkıştır", icon, Command("--compress balanced"));
        AddCommand(writes, CompressMenu, "03zip", "zip olarak sıkıştır", icon, Command("--compress share"));

        foreach (var extension in Extensions)
            AddCascade(writes, $@"{Classes}\SystemFileAssociations\{extension}\shell\{ArchiveVerb}", "AFU Zip", icon, ArchiveMenu);

        AddCascade(writes, $@"{Classes}\*\shell\{CompressVerb}", "AFU Zip ile sıkıştır", icon, CompressMenu);
        AddCascade(writes, $@"{Classes}\Directory\shell\{CompressVerb}", "AFU Zip ile sıkıştır", icon, CompressMenu);
        return writes;
    }

    /// <summary>
    /// Tamamen uygulamaya ait, kaldırırken bütünüyle silinen anahtarlar. Her iki menü biçimini de kapsar; kayıttan
    /// önce de silinir ki sabit menüden eklentiye (ya da tersi) geçişte iki "AFU Zip" öğesi kalmasın.
    /// </summary>
    public static IReadOnlyList<string> OwnedKeys(string exePath)
    {
        var keys = new List<string>
        {
            HandlerKey,
            $@"{Classes}\*\shell\{ArchiveVerb}",
            $@"{Classes}\Directory\shell\{ArchiveVerb}",
            $@"{Classes}\{ProgId}",
            $@"{Classes}\{ArchiveMenu}",
            $@"{Classes}\{CompressMenu}",
            $@"{Classes}\Applications\{Path.GetFileName(exePath)}",
            $@"{Classes}\*\shell\{CompressVerb}",
            $@"{Classes}\Directory\shell\{CompressVerb}",
            CapabilitiesKey,
        };
        keys.AddRange(Extensions.Select(e => $@"{Classes}\SystemFileAssociations\{e}\shell\{ArchiveVerb}"));
        return keys;
    }

    /// <summary>Başka uygulamalarla paylaşılan anahtarlardaki değerler: yalnızca bizimki silinir.</summary>
    public static IReadOnlyList<RegistryValueRemoval> SharedValues() =>
    [
        new(RegisteredApplications, RegisteredName),
        new(AppKey, ExePathValue),
        .. Extensions.Select(e => new RegistryValueRemoval($@"{Classes}\{e}\OpenWithProgids", ProgId)),
    ];

    private static void AddCascade(List<RegistryWrite> writes, string key, string title, string icon, string subCommands)
    {
        writes.Add(new(key, "MUIVerb", title));
        writes.Add(new(key, "Icon", icon));
        writes.Add(new(key, "ExtendedSubCommandsKey", subCommands));
    }

    private static void AddCommand(List<RegistryWrite> writes, string menu, string id, string title, string icon, string command,
        int flags = 0)
    {
        var key = $@"{Classes}\{menu}\shell\{id}";
        writes.Add(new(key, "MUIVerb", title));
        writes.Add(new(key, "Icon", icon));
        // Varsayılan "Document" modeli 15'ten fazla seçimde komutu gizler.
        writes.Add(new(key, "MultiSelectModel", "Player"));
        if (flags != 0)
            writes.Add(new(key, "CommandFlags", flags, RegistryKind.DWord));
        writes.Add(new($@"{key}\command", null, command));
    }
}

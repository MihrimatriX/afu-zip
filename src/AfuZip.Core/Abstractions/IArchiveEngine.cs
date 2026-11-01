namespace AfuZip.Core;

/// <summary>Bir sıkıştırma motoru (7z.dll, yönetilen .NET kodu...). Motorlar kayıt sırasına göre önceliklidir.</summary>
public interface IArchiveEngine
{
    string Name { get; }

    /// <summary>Motor bu makinede kullanılabilir mi (ör. 7z.dll bulundu mu).</summary>
    bool IsAvailable { get; }

    /// <summary><see cref="ArchiveFormat.Unknown"/> için true dönen motor, formatı kendisi deneyerek bulabilir.</summary>
    bool Supports(ArchiveFormat format, Capability needed);

    Task<IArchiveReader> OpenAsync(string archivePath, OpenOptions options, CancellationToken ct = default);

    Task CreateAsync(CreateRequest request, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Var olan arşivi değiştirir (ekle, sil, yeniden adlandır). Yeni arşiv geçici dosyaya yazılır; dönen nesnenin
    /// <see cref="PendingArchive.Commit"/>'i eskisinin yerine koyar. Arada çağıran, arşive açık kendi okuyucularını
    /// kapatmalıdır (Windows açık dosyanın üzerine yazdırmaz).
    /// </summary>
    Task<PendingArchive> PrepareUpdateAsync(string archivePath, ArchiveUpdate update, OpenOptions options,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) =>
        throw new NotSupportedException($"{Name} motoru arşiv düzenlemeyi desteklemiyor.");
}

/// <summary>Yazılmış ama henüz yerine konmamış arşiv. Commit edilmeden atılırsa geçici dosyalar silinir.</summary>
public sealed class PendingArchive(OutputTarget target) : IDisposable
{
    public string ArchivePath => target.FinalPath;

    public void Commit() => target.Commit();

    public void Dispose() => target.Dispose();
}

public interface IArchiveReader : IAsyncDisposable
{
    string ArchivePath { get; }

    /// <summary>Kullanıcıya gösterilen ad; iç içe arşivlerde "dış.zip › iç.tar" biçiminde.</summary>
    string DisplayName => Path.GetFileName(ArchivePath);
    ArchiveFormat Format { get; }
    string FormatName { get; }
    string EngineName { get; }
    IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>Solid arşivde tek dosyayı açmak, aynı bloktaki önceki dosyaları da açmayı gerektirir.</summary>
    bool IsSolid { get; }

    /// <summary>
    /// Arşiv açıldı ama sorunlu: beklenmedik son (eksik cilt, yarım indirme), sonda fazladan veri,
    /// hatalı başlıklar. Kullanıcıya gösterilir; içerik listesi eksik olabilir.
    /// </summary>
    IReadOnlyList<string> Warnings => [];

    /// <summary>Tek bir girdiyi diske yazmadan, başa sarılmış ve seek edilebilir bir stream olarak açar.</summary>
    Task<Stream> OpenEntryAsync(int index, CancellationToken ct = default);

    /// <summary>
    /// <see cref="ReadEntriesAsync"/> girdileri gerçekten tek geçişte okuyor mu (7-Zip). false ise varsayılan uygulama
    /// girdileri tek tek açar; yönetilen tar.gz okuyucusunda bu her girdi için baştan açmak demektir, önbellek
    /// bu durumda blok açmaya kalkışmaz.
    /// </summary>
    bool SupportsBatchRead => false;

    /// <summary>
    /// Birden çok girdiyi tek geçişte alıcıya yazar. Solid arşivde blok yalnızca bir kez çözülür (7-Zip motoru);
    /// varsayılan uygulama girdileri tek tek açar. Girdi bazındaki hatalar alıcıya bildirilir, istisna atılmaz.
    /// </summary>
    async Task ReadEntriesAsync(IReadOnlyCollection<int> indices, IEntryReceiver receiver,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        var ordered = indices.Distinct().Order().ToList();
        long total = ordered.Sum(i => Entries[i].Size), done = 0;

        foreach (var index in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var entry = Entries[index];
            if (entry.IsDirectory || receiver.Begin(entry) is not { } target)
                continue;

            try
            {
                await using (var source = await OpenEntryAsync(index, ct).ConfigureAwait(false))
                    await source.CopyToAsync(target, ct).ConfigureAwait(false);
                receiver.End(entry, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                receiver.End(entry, ex.Message);
            }

            done += entry.Size;
            progress?.Report(new(done, total, entry.Path));
        }
    }

    /// <param name="indices">null ise tüm girdiler.</param>
    Task<ExtractionResult> ExtractAsync(
        IReadOnlyCollection<int>? indices,
        ExtractionOptions options,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Girdileri diske yazmadan açıp bütünlüğünü (CRC) doğrular.</summary>
    Task<TestResult> TestAsync(
        IReadOnlyCollection<int>? indices = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary><see cref="IArchiveReader.ReadEntriesAsync"/> için hedef: her girdiye bir yazma stream'i verir.</summary>
public interface IEntryReceiver
{
    /// <returns>Girdinin yazılacağı stream; null ise girdi atlanır.</returns>
    Stream? Begin(ArchiveEntry entry);

    /// <summary>Begin'in stream verdiği her girdi için bir kez çağrılır.</summary>
    /// <param name="error">null: başarılı; aksi halde kullanıcıya gösterilebilir neden.</param>
    void End(ArchiveEntry entry, string? error);
}

public interface IPasswordProvider
{
    /// <returns>null: kullanıcı vazgeçti.</returns>
    ValueTask<string?> GetPasswordAsync(string archivePath, CancellationToken ct = default);

    /// <summary>Verilen parola yanlış çıktı; sağlayıcı önbelleğini temizlemeli ve bir sonraki istekte yeniden sormalı.</summary>
    void OnPasswordRejected(string archivePath)
    {
    }
}

public sealed class StaticPasswordProvider(string? password) : IPasswordProvider
{
    public ValueTask<string?> GetPasswordAsync(string archivePath, CancellationToken ct = default) => ValueTask.FromResult(password);
}

public sealed record OpenOptions(ArchiveFormat FormatHint = ArchiveFormat.Unknown, IPasswordProvider? Passwords = null)
{
    public static OpenOptions Default { get; } = new();
}

/// <param name="Password">Verilirse içerik şifrelenir (7z ve zip: AES-256).</param>
/// <param name="EncryptHeaders">7z: dosya adları da şifrelenir; parolasız liste bile görülemez.</param>
/// <param name="VolumeSize">Verilirse arşiv bu boyutta ciltlere bölünür (ad.7z.001, .002...).</param>
/// <param name="Threads">null: işlemci sayısı kadar.</param>
public sealed record CreateRequest(
    string ArchivePath,
    IReadOnlyList<string> Sources,
    CompressionPreset Preset,
    string? Password = null,
    bool EncryptHeaders = false,
    long? VolumeSize = null,
    int? Threads = null);

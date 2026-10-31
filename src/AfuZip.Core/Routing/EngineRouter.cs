namespace AfuZip.Core;

/// <summary>
/// Format ve istenen yeteneğe göre motor seçer. Kayıt sırası önceliktir:
/// 7z.dll varsa o, yoksa yönetilen motor.
/// </summary>
public sealed class EngineRouter(IEnumerable<IArchiveEngine> engines)
{
    public IReadOnlyList<IArchiveEngine> Engines { get; } = engines.ToArray();

    public IArchiveEngine? TryResolve(ArchiveFormat format, Capability needed) =>
        Engines.FirstOrDefault(e => e.IsAvailable && e.Supports(format, needed));

    public IArchiveEngine Resolve(ArchiveFormat format, Capability needed) =>
        TryResolve(format, needed)
        ?? throw new NotSupportedException($"{format.DisplayName()} için '{needed}' destekleyen bir motor yok.");

    /// <summary>
    /// Formatı tespit eder ve uygun motorlarla sırayla açmayı dener. Bir motor açamazsa
    /// (bozuk başlık, desteklenmeyen varyant) bir sonrakine geçilir; parola hataları ise hemen yukarı iletilir.
    /// </summary>
    public async Task<IArchiveReader> OpenAsync(string archivePath, OpenOptions? options = null, CancellationToken ct = default)
    {
        options ??= OpenOptions.Default;
        var format = options.FormatHint != ArchiveFormat.Unknown ? options.FormatHint : FormatSniffer.Detect(archivePath);
        options = options with { FormatHint = format };

        var candidates = Engines.Where(e => e.IsAvailable && e.Supports(format, Capability.Read)).ToList();
        if (candidates.Count == 0)
            throw new NotSupportedException($"'{Path.GetFileName(archivePath)}' ({format.DisplayName()}) açabilecek bir motor yok.");

        var errors = new List<Exception>();
        foreach (var engine in candidates)
        {
            try
            {
                return await engine.OpenAsync(archivePath, options, ct).ConfigureAwait(false);
            }
            catch (ArchivePasswordException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
            {
                errors.Add(ex);
            }
        }

        throw new AggregateException($"'{Path.GetFileName(archivePath)}' hiçbir motorla açılamadı.", errors);
    }
}

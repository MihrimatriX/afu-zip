using AfuZip.Core;

namespace AfuZip.Engine.Managed;

internal abstract class ManagedReaderBase(string archivePath, ArchiveFormat format, string formatName, IReadOnlyList<ArchiveEntry> entries)
    : IArchiveReader
{
    /// <summary>Bu boyutun altındaki girdiler belleğe, üstündekiler geçici dosyaya açılır.</summary>
    internal const long InMemoryLimit = 64L * 1024 * 1024;

    protected readonly SemaphoreSlim Gate = new(1, 1);

    public string ArchivePath { get; } = archivePath;
    public ArchiveFormat Format { get; } = format;
    public string FormatName { get; } = formatName;
    public string EngineName => "managed";
    public IReadOnlyList<ArchiveEntry> Entries { get; } = entries;
    public virtual bool IsSolid => false;

    public async Task<Stream> OpenEntryAsync(int index, CancellationToken ct = default)
    {
        var entry = Entries[index];
        if (entry.IsDirectory)
            throw new InvalidOperationException($"'{entry.Path}' bir klasör.");

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var buffer = CreateBuffer(entry.Size);
            try
            {
                await CopyEntryAsync(index, buffer, ct).ConfigureAwait(false);
                buffer.Position = 0;
                return buffer;
            }
            catch
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<ExtractionResult> ExtractAsync(IReadOnlyCollection<int>? indices, ExtractionOptions options,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        var selected = Select(indices);

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ExtractCoreAsync(selected, new ExtractionSession(ArchivePath, options), progress, ct), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<TestResult> TestAsync(IReadOnlyCollection<int>? indices = null, IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        var selected = Select(indices);

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => TestCoreAsync(selected, progress, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Girdinin baytlarını hedef stream'e kopyalar.</summary>
    protected abstract Task CopyEntryAsync(int index, Stream destination, CancellationToken ct);

    protected abstract Task<ExtractionResult> ExtractCoreAsync(IReadOnlyList<ArchiveEntry> selected, ExtractionSession session,
        IProgress<ArchiveProgress>? progress, CancellationToken ct);

    /// <summary>Varsayılan: her girdiyi ayrı ayrı açar. İleri doğru okunan formatlar (tar) tek geçişle override eder.</summary>
    protected virtual async Task<TestResult> TestCoreAsync(IReadOnlyList<ArchiveEntry> selected,
        IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var result = new TestResult();
        var total = selected.Sum(e => e.Size);
        long done = 0;
        foreach (var entry in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory || entry.IsSymlink)
                continue;

            await TestEntryAsync(entry, target => CopyEntryAsync(entry.Index, target, ct), result).ConfigureAwait(false);
            done += entry.Size;
            progress?.Report(new(done, total, entry.Path));
        }
        return result;
    }

    /// <summary>Girdiyi CRC hesaplayan bir stream'e akıtır ve arşivdeki CRC ile karşılaştırır.</summary>
    protected static async Task TestEntryAsync(ArchiveEntry entry, Func<Stream, Task> copyTo, TestResult result)
    {
        if (entry.IsEncrypted)
        {
            result.AddFailed(entry.Path, "şifreli ZIP girdileri yönetilen motorla doğrulanamaz; 7-Zip motoru gerekir");
            return;
        }

        var crc = new Crc32Stream();
        try
        {
            await copyTo(crc).ConfigureAwait(false);
            if (entry.Crc is { } expected && crc.Value != expected)
                result.AddFailed(entry.Path, $"CRC hatası (beklenen {expected:X8}, hesaplanan {crc.Value:X8})");
            else
                result.AddTested();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            result.AddFailed(entry.Path, ex.Message);
        }
    }

    private IReadOnlyList<ArchiveEntry> Select(IReadOnlyCollection<int>? indices) => indices is null
        ? Entries
        : indices.Distinct().Order().Select(i => Entries[i]).ToList();

    /// <summary>Tek bir dosya girdisini oturum kurallarıyla diske yazar.</summary>
    protected static async Task WriteFileAsync(ExtractionSession session, ArchiveEntry entry, Func<Stream> openSource, CancellationToken ct)
    {
        var target = session.BeginFile(entry);
        if (target is null)
            return;

        try
        {
            await using (var source = openSource())
            await using (var output = session.OpenOutput(target))
                await source.CopyToAsync(output, ct).ConfigureAwait(false);
            session.CompleteFile(entry, target);
        }
        catch (ExtractionLimitExceededException ex)
        {
            session.FailFile(entry, target, ex.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            session.FailFile(entry, target, "iptal edildi");
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            session.FailFile(entry, target, ex.Message);
        }
    }

    // Boyutu bilinmeyen (0 bildirilen) girdiler sınırsız büyüyebileceği için geçici dosyaya açılır.
    private static Stream CreateBuffer(long size) => size is > 0 and <= InMemoryLimit
        ? new MemoryStream((int)size)
        : new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);

    public virtual ValueTask DisposeAsync()
    {
        Gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

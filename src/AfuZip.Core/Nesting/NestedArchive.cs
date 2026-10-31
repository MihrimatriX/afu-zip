namespace AfuZip.Core;

/// <summary>
/// Arşiv içindeki arşivi (zip içinde zip, tar.gz'nin içindeki tar, .7z.001 parçalarının birleşimi) açar.
/// İç arşiv geçici klasöre güvenli çıkarma kurallarıyla açılır; MotW dış arşivden iç arşive geçer.
/// </summary>
public static class NestedArchive
{
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "AfuZip", "nested");

    public static bool LooksLikeArchive(ArchiveEntry entry) =>
        !entry.IsDirectory &&
        (FormatSniffer.FromExtension(entry.Name) != ArchiveFormat.Unknown || IsFirstVolume(entry.Name));

    public static bool IsFirstVolume(string name) =>
        name.EndsWith(".001", StringComparison.OrdinalIgnoreCase);

    /// <param name="ownsOuter">true ise iç arşiv kapanınca dış arşiv de kapanır (otomatik açılan tar.gz gibi).</param>
    public static async Task<IArchiveReader> OpenAsync(EngineRouter router, IArchiveReader outer, int index,
        OpenOptions? options, bool ownsOuter, CancellationToken ct = default)
    {
        var entry = outer.Entries[index];
        var tempDir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        try
        {
            var result = await outer.ExtractAsync([index], new ExtractionOptions(tempDir), progress: null, ct).ConfigureAwait(false);
            if (result.Extracted != 1)
            {
                var reason = result.Failed.FirstOrDefault()?.Reason ?? result.Blocked.FirstOrDefault()?.Reason ?? "çıkarılamadı";
                throw new InvalidDataException($"İç arşiv '{entry.Path}' açılamadı: {reason}");
            }

            var innerPath = SafePath.ResolveTarget(tempDir, entry.Path);
            var inner = await router.OpenAsync(innerPath, options, ct).ConfigureAwait(false);
            return new NestedArchiveReader(inner, outer, ownsOuter, tempDir, $"{outer.DisplayName} › {entry.Path}");
        }
        catch
        {
            TryDeleteDirectory(tempDir);
            throw;
        }
    }

    /// <summary>Uygulama çökerse kalan eski geçici klasörleri temizler.</summary>
    public static void CleanupStale(TimeSpan olderThan)
    {
        if (!Directory.Exists(TempRoot))
            return;
        foreach (var dir in Directory.EnumerateDirectories(TempRoot))
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > olderThan)
                TryDeleteDirectory(dir);
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class NestedArchiveReader(IArchiveReader inner, IArchiveReader outer, bool ownsOuter, string tempDir, string displayName)
    : IArchiveReader
{
    public IArchiveReader Outer => outer;
    public string ArchivePath => inner.ArchivePath;
    public string DisplayName => displayName;
    public ArchiveFormat Format => inner.Format;
    public string FormatName => $"{outer.FormatName} › {inner.FormatName}";
    public string EngineName => inner.EngineName;
    public IReadOnlyList<ArchiveEntry> Entries => inner.Entries;
    public bool IsSolid => inner.IsSolid;
    public IReadOnlyList<string> Warnings => [.. outer.Warnings, .. inner.Warnings];
    public bool SupportsBatchRead => inner.SupportsBatchRead;

    public Task<Stream> OpenEntryAsync(int index, CancellationToken ct = default) => inner.OpenEntryAsync(index, ct);

    public Task ReadEntriesAsync(IReadOnlyCollection<int> indices, IEntryReceiver receiver,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) =>
        inner.ReadEntriesAsync(indices, receiver, progress, ct);

    public Task<ExtractionResult> ExtractAsync(IReadOnlyCollection<int>? indices, ExtractionOptions options,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) =>
        inner.ExtractAsync(indices, options, progress, ct);

    public Task<TestResult> TestAsync(IReadOnlyCollection<int>? indices = null, IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default) =>
        inner.TestAsync(indices, progress, ct);

    public async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        if (ownsOuter)
            await outer.DisposeAsync().ConfigureAwait(false);
        NestedArchive.TryDeleteDirectory(tempDir);
    }
}

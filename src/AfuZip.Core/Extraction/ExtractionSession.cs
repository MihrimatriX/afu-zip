namespace AfuZip.Core;

/// <summary>
/// Motorlardan bağımsız, güvenli çıkarma mantığı. Motor yalnızca girdinin baytlarını üretir;
/// yolun doğrulanması, üzerine yazma kararı, limitler, zaman damgası ve MotW burada uygulanır.
/// Thread-safe değildir; bir çıkarma işlemi tek bir thread'den sürülür.
/// </summary>
public sealed class ExtractionSession
{
    private readonly ExtractionOptions _options;
    private readonly string _root;
    private readonly string? _zoneIdentifier;
    private readonly long _archiveSize;
    private int _entryCount;

    public ExtractionSession(string archivePath, ExtractionOptions options)
    {
        _options = options;
        _root = Path.GetFullPath(options.DestinationDirectory);
        Directory.CreateDirectory(_root);
        _archiveSize = Math.Max(1, new FileInfo(archivePath).Length);
        _zoneIdentifier = options.PropagateMarkOfTheWeb ? MarkOfTheWeb.TryRead(archivePath) : null;
    }

    public ExtractionResult Result { get; } = new();

    /// <summary>Dosya girdisi için hedef yolu hazırlar. null: girdi atlandı veya engellendi (sonuca işlendi).</summary>
    public string? BeginFile(ArchiveEntry entry)
    {
        CountEntry();

        if (entry.IsSymlink && _options.Symlinks == SymlinkPolicy.Skip)
        {
            Result.AddBlocked(entry.Path, "sembolik link varsayılan olarak oluşturulmaz");
            return null;
        }

        string target;
        try
        {
            target = SafePath.ResolveTarget(_root, entry.Path);
        }
        catch (UnsafePathException ex)
        {
            Result.AddBlocked(entry.Path, ex.Reason);
            return null;
        }

        if (File.Exists(target))
        {
            switch (_options.Overwrite)
            {
                case OverwritePolicy.Skip:
                    Result.AddSkipped();
                    return null;
                case OverwritePolicy.Rename:
                    target = UniqueName(target);
                    break;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return target;
    }

    public void CreateDirectory(ArchiveEntry entry)
    {
        CountEntry();
        try
        {
            var target = SafePath.ResolveTarget(_root, entry.Path);
            Directory.CreateDirectory(target);
            if (_options.PreserveTimestamps && entry.Modified is { } modified)
                TrySetTime(() => Directory.SetLastWriteTimeUtc(target, modified.UtcDateTime));
            Result.AddDirectory();
        }
        catch (UnsafePathException ex)
        {
            Result.AddBlocked(entry.Path, ex.Reason);
        }
    }

    /// <summary>Limitleri sayan bir yazma stream'i açar. Stream kapatıldıktan sonra <see cref="CompleteFile"/> çağrılmalı.</summary>
    public Stream OpenOutput(string targetPath) =>
        new GuardedWriteStream(new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16), this);

    public void CompleteFile(ArchiveEntry entry, string targetPath)
    {
        if (_options.PreserveTimestamps && entry.Modified is { } modified)
            TrySetTime(() => File.SetLastWriteTimeUtc(targetPath, modified.UtcDateTime));
        MarkOfTheWeb.TryApply(targetPath, _zoneIdentifier);
        Result.AddExtracted();
    }

    /// <summary>Yarım kalmış dosyayı siler ve hatayı sonuca işler.</summary>
    public void FailFile(ArchiveEntry entry, string? targetPath, string reason)
    {
        if (targetPath is not null)
        {
            try { File.Delete(targetPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Result.AddFailed(entry.Path, reason);
    }

    public void Block(ArchiveEntry entry, string reason) => Result.AddBlocked(entry.Path, reason);

    internal void OnBytesWritten(int count)
    {
        Result.BytesWritten += count;
        var limits = _options.Limits;

        if (Result.BytesWritten > limits.MaxTotalBytes)
            throw new ExtractionLimitExceededException(
                $"Toplam çıktı {limits.MaxTotalBytes:N0} bayt sınırını aştı.");

        if (Result.BytesWritten > limits.RatioCheckThresholdBytes &&
            (double)Result.BytesWritten / _archiveSize > limits.MaxExpansionRatio)
            throw new ExtractionLimitExceededException(
                $"Arşiv boyutunun {limits.MaxExpansionRatio:N0} katından fazla veri üretiyor; zip bomb olabilir.");
    }

    private void CountEntry()
    {
        if (++_entryCount > _options.Limits.MaxEntries)
            throw new ExtractionLimitExceededException($"Girdi sayısı {_options.Limits.MaxEntries:N0} sınırını aştı.");
    }

    private static string UniqueName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static void TrySetTime(Action set)
    {
        try { set(); }
        catch (ArgumentOutOfRangeException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class GuardedWriteStream(Stream inner, ExtractionSession session) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        session.OnBytesWritten(buffer.Length);
        inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        session.OnBytesWritten(buffer.Length);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}

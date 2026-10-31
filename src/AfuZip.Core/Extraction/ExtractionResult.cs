namespace AfuZip.Core;

public sealed record ExtractionIssue(string EntryPath, string Reason);

public sealed class ExtractionResult
{
    private readonly List<ExtractionIssue> _blocked = [];
    private readonly List<ExtractionIssue> _failed = [];

    public int Extracted { get; private set; }
    public int Directories { get; private set; }
    public int Skipped { get; private set; }
    public long BytesWritten { get; internal set; }

    /// <summary>Güvenlik nedeniyle yazılmayan girdiler (yol aşımı, symlink...).</summary>
    public IReadOnlyList<ExtractionIssue> Blocked => _blocked;

    /// <summary>Okuma veya yazma hatası alan girdiler (CRC, yanlış parola...).</summary>
    public IReadOnlyList<ExtractionIssue> Failed => _failed;

    public bool Success => _failed.Count == 0;

    internal void AddExtracted() => Extracted++;
    internal void AddDirectory() => Directories++;
    internal void AddSkipped() => Skipped++;
    internal void AddBlocked(string path, string reason) => _blocked.Add(new(path, reason));
    internal void AddFailed(string path, string reason) => _failed.Add(new(path, reason));

    public override string ToString() =>
        $"{Extracted} dosya, {Directories} klasör çıkarıldı; {Skipped} atlandı, {Blocked.Count} engellendi, {Failed.Count} hata.";
}

public sealed class UnsafePathException(string entryPath, string reason)
    : IOException($"Güvensiz yol engellendi: '{entryPath}' ({reason})")
{
    public string EntryPath { get; } = entryPath;
    public string Reason { get; } = reason;
}

public sealed class ExtractionLimitExceededException(string message) : IOException(message);

public sealed class ArchivePasswordException(string archivePath, bool wasProvided)
    : IOException(wasProvided ? $"Parola yanlış: {archivePath}" : $"Bu arşiv parola istiyor: {archivePath}")
{
    public bool WasProvided { get; } = wasProvided;
}

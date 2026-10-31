namespace AfuZip.Core;

public enum OverwritePolicy
{
    Overwrite,
    Skip,
    /// <summary>"dosya (2).txt" gibi yeni bir ad üretir.</summary>
    Rename,
}

public enum SymlinkPolicy
{
    /// <summary>Varsayılan: sembolik linkler oluşturulmaz (CVE-2025-11001/11002 sınıfı saldırılar).</summary>
    Skip,
    /// <summary>Hedefi de çıkarma klasörünün içinde kalıyorsa oluşturulur. Henüz uygulanmadı.</summary>
    CreateIfInside,
}

/// <summary>Zip bomb ve kaynak tüketimine karşı sınırlar.</summary>
public sealed record ExtractionLimits(
    long MaxTotalBytes = 256L * 1024 * 1024 * 1024,
    int MaxEntries = 2_000_000,
    double MaxExpansionRatio = 1000,
    long RatioCheckThresholdBytes = 64L * 1024 * 1024)
{
    public static ExtractionLimits Default { get; } = new();
}

public sealed record ExtractionOptions(string DestinationDirectory)
{
    public OverwritePolicy Overwrite { get; init; } = OverwritePolicy.Rename;
    public SymlinkPolicy Symlinks { get; init; } = SymlinkPolicy.Skip;
    public ExtractionLimits Limits { get; init; } = ExtractionLimits.Default;
    public bool PreserveTimestamps { get; init; } = true;

    /// <summary>İnternetten gelen arşivin Mark-of-the-Web işaretini çıkarılan dosyalara aktarır (CVE-2025-0411).</summary>
    public bool PropagateMarkOfTheWeb { get; init; } = true;

    public IPasswordProvider? Passwords { get; init; }
}

namespace AfuZip.Core;

[Flags]
public enum Capability
{
    None = 0,
    Read = 1,
    Write = 2,
    Encrypt = 4,
    Update = 8,
}

public readonly record struct ArchiveProgress(long CompletedBytes, long TotalBytes, string? CurrentItem = null)
{
    public double? Fraction => TotalBytes > 0 ? Math.Clamp((double)CompletedBytes / TotalBytes, 0, 1) : null;
}

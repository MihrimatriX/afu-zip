namespace AfuZip.Core;

/// <summary>"Test et": girdiler diske yazılmadan açılır ve CRC/bütünlük doğrulanır.</summary>
public sealed class TestResult
{
    private readonly List<ExtractionIssue> _failed = [];

    public int Tested { get; private set; }
    public IReadOnlyList<ExtractionIssue> Failed => _failed;
    public bool Success => _failed.Count == 0;

    public void AddTested() => Tested++;
    public void AddFailed(string entryPath, string reason) => _failed.Add(new(entryPath, reason));

    public override string ToString() => Success
        ? $"Hata yok: {Tested} dosya doğrulandı."
        : $"{_failed.Count} dosyada hata, {Tested} dosya sağlam.";
}

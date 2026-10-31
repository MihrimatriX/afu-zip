namespace AfuZip.Core;

/// <summary>
/// İnternetten indirilen arşivlerin "Zone.Identifier" alternatif veri akışını çıkarılan dosyalara taşır.
/// Faz 4'te IAttachmentExecute'a geçilecek; şimdilik ADS doğrudan kopyalanır.
/// </summary>
public static class MarkOfTheWeb
{
    private const string StreamSuffix = ":Zone.Identifier";

    public static string? TryRead(string archivePath)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return File.ReadAllText(archivePath + StreamSuffix);
        }
        catch (IOException)
        {
            return null; // arşiv internetten gelmemiş
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void TryApply(string filePath, string? zoneIdentifier)
    {
        if (zoneIdentifier is null || !OperatingSystem.IsWindows())
            return;
        try
        {
            File.WriteAllText(filePath + StreamSuffix, zoneIdentifier);
        }
        catch (IOException)
        {
            // FAT32/exFAT gibi ADS desteklemeyen sürücüler
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

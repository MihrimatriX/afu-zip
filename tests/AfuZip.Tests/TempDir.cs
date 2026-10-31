using System.Security.Cryptography;

namespace AfuZip.Tests;

/// <summary>Her test için ayrı, test bitince silinen geçici klasör.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "afuzip-tests", Guid.NewGuid().ToString("N"));

    public string this[string relative] => System.IO.Path.Combine(Path, relative);

    public string WriteFile(string relative, string content)
    {
        var full = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string WriteFile(string relative, byte[] content)
    {
        var full = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    /// <summary>Göreli yol → SHA-256 eşlemesi; iki klasörün içerik olarak aynı olduğunu doğrulamak için.</summary>
    public static Dictionary<string, string> Hashes(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            f => System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'),
            f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

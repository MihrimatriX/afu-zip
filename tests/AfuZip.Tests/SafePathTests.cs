namespace AfuZip.Tests;

public class SafePathTests
{
    private static readonly char Sep = Path.DirectorySeparatorChar;

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("a\\..\\..\\evil.txt")]
    [InlineData("..")]
    public void ParentTraversal_IsRejected(string entryPath) =>
        Assert.Throws<UnsafePathException>(() => SafePath.SanitizeRelative(entryPath));

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("./.")]
    public void EmptyPath_IsRejected(string entryPath) =>
        Assert.Throws<UnsafePathException>(() => SafePath.SanitizeRelative(entryPath));

    [Fact]
    public void AbsolutePaths_StayInsideRoot()
    {
        Assert.Equal($"etc{Sep}passwd", SafePath.SanitizeRelative("/etc/passwd"));
        Assert.Equal($"C_{Sep}Windows{Sep}win.ini", SafePath.SanitizeRelative("C:\\Windows\\win.ini"));
    }

    [Fact]
    public void AlternateDataStream_IsNeutralized()
    {
        Assert.Equal("a.txt_Zone.Identifier", SafePath.SanitizeRelative("a.txt:Zone.Identifier"));
        Assert.Equal("_Zone.Identifier_$DATA", SafePath.SanitizeRelative(":Zone.Identifier:$DATA"));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1.log", "_COM1.log")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("rapor.txt.", "rapor.txt_")]
    [InlineData("klasör  ", "klasör__")]
    [InlineData("a<b>c|d?.txt", "a_b_c_d_.txt")]
    public void Segments_AreMadeWindowsSafe(string segment, string expected) =>
        Assert.Equal(expected, SafePath.SanitizeSegment(segment));

    [Fact]
    public void TurkishNames_ArePreserved() =>
        Assert.Equal($"Belgeler{Sep}çiçek ağacı{Sep}ŞĞÜÖÇİ ı.txt", SafePath.SanitizeRelative("Belgeler/çiçek ağacı/ŞĞÜÖÇİ ı.txt"));

    [Fact]
    public void ResolveTarget_ReturnsPathInsideRoot()
    {
        using var temp = new TempDir();
        var target = SafePath.ResolveTarget(temp.Path, "./alt/./dosya.txt");
        Assert.Equal(Path.Combine(temp.Path, "alt", "dosya.txt"), target);
    }
}

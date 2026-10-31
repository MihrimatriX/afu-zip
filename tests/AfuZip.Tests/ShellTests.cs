namespace AfuZip.Tests;

public class ShellTests
{
    [Fact]
    public void Parse_NoFlag_IsOpen()
    {
        using var temp = new TempDir();
        var archive = temp.WriteFile("a.7z", "x");
        var command = ShellCommand.Parse([archive]);
        Assert.Equal(ShellVerb.Open, command.Verb);
        Assert.Equal([archive], command.Paths);
        Assert.False(command.IsBackgroundAction);
    }

    [Theory]
    [InlineData("--extract-here", ShellVerb.ExtractHere)]
    [InlineData("--EXTRACT-FOLDER", ShellVerb.ExtractToFolder)]
    [InlineData("--test", ShellVerb.Test)]
    [InlineData("--add", ShellVerb.AddDialog)]
    public void Parse_Flags(string flag, ShellVerb verb)
    {
        var command = ShellCommand.Parse([flag, "dosya.zip"]);
        Assert.Equal(verb, command.Verb);
        Assert.Equal(Path.GetFullPath("dosya.zip"), Assert.Single(command.Paths));
    }

    [Fact]
    public void Parse_CompressTakesPreset_AndIgnoresUnknownFlags()
    {
        var command = ShellCommand.Parse(["--compress", "Share", "--yeni-bayrak", "Belgeler"]);
        Assert.Equal(ShellVerb.Compress, command.Verb);
        Assert.Equal("share", command.PresetId);
        Assert.Equal(Path.GetFullPath("Belgeler"), Assert.Single(command.Paths));
        Assert.True(command.IsBackgroundAction);
        Assert.True(command.AcceptsFolders);
    }

    [Fact]
    public void Parse_TrimsQuotesAndTrailingSeparator()
    {
        var folder = Path.Combine(Path.GetTempPath(), "afuzip klasör");
        var command = ShellCommand.Parse(["--add", $"\"{folder}{Path.DirectorySeparatorChar}\""]);
        Assert.Equal(folder, Assert.Single(command.Paths));
    }

    [Fact]
    public void Merge_CombinesSameCompressRequests_WithoutDuplicates()
    {
        var a = ShellCommand.Parse(["--compress", "balanced", "a.txt"]);
        var b = ShellCommand.Parse(["--compress", "balanced", "b.txt", "a.txt"]);
        var zip = ShellCommand.Parse(["--compress", "share", "c.txt"]);
        var extract = ShellCommand.Parse(["--extract-here", "d.7z"]);

        Assert.True(a.CanMergeWith(b));
        Assert.False(a.CanMergeWith(zip));
        Assert.False(extract.CanMergeWith(extract));
        Assert.Equal([Path.GetFullPath("a.txt"), Path.GetFullPath("b.txt")], a.MergeWith(b).Paths);
    }

    [Fact]
    public void DefaultTarget_SingleFolder_SingleFile_Multiple()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp["Belgeler"]);
        var report = temp.WriteFile("rapor.docx", "x");
        var other = temp.WriteFile("not.txt", "x");

        Assert.Equal(temp["Belgeler.7z"], ArchiveNaming.DefaultTarget([temp["Belgeler"]], ".7z"));
        Assert.Equal(temp["rapor.zip"], ArchiveNaming.DefaultTarget([report], ".zip"));
        Assert.Equal(temp[Path.GetFileName(temp.Path) + ".7z"], ArchiveNaming.DefaultTarget([report, other], ".7z"));
        Assert.Equal(Path.Combine("/masaüstü", "Arşiv.7z"), ArchiveNaming.DefaultTarget([], ".7z", "/masaüstü"));
    }

    [Fact]
    public void Unique_SkipsExistingFilesAndVolumes_KeepsCompoundExtension()
    {
        using var temp = new TempDir();
        temp.WriteFile("yedek.7z", "x");
        temp.WriteFile("yedek (2).7z.001", "x");
        temp.WriteFile("proje.tar.gz", "x");

        Assert.Equal(temp["yedek (3).7z"], ArchiveNaming.Unique(temp["yedek.7z"]));
        Assert.Equal(temp["proje (2).tar.gz"], ArchiveNaming.Unique(temp["proje.tar.gz"]));
        Assert.Equal(temp["yeni.zip"], ArchiveNaming.Unique(temp["yeni.zip"]));
    }

    [Fact]
    public void ExtensionFor_Presets()
    {
        Assert.Equal(".7z", ArchiveNaming.ExtensionFor(CompressionPreset.Balanced));
        Assert.Equal(".zip", ArchiveNaming.ExtensionFor(CompressionPreset.Share));
        Assert.Equal(".tar.zst", ArchiveNaming.ExtensionFor(CompressionPreset.Developer));
        Assert.Equal("yedek.zip", ArchiveNaming.ReplaceExtension("yedek.tar.gz", ".zip"));
    }

    [Fact]
    public void ForEngines_FallsBackToWhatIsInstalled()
    {
        Assert.Same(CompressionPreset.Share, CompressionPreset.ForEngines("balanced", hasSevenZip: false, hasZstd: false));
        Assert.Same(CompressionPreset.Share, CompressionPreset.ForEngines("share", hasSevenZip: false, hasZstd: false));
        Assert.Same(CompressionPreset.Balanced, CompressionPreset.ForEngines("dev", hasSevenZip: true, hasZstd: false));
        Assert.Same(CompressionPreset.Balanced, CompressionPreset.ForEngines("bilinmeyen", hasSevenZip: true, hasZstd: true));
        var fast = CompressionPreset.ForEngines("fast", hasSevenZip: true, hasZstd: false);
        Assert.Equal(("LZMA2", ArchiveFormat.SevenZip), (fast.Method, fast.Format));
        Assert.Same(CompressionPreset.Fast, CompressionPreset.ForEngines("FAST", hasSevenZip: true, hasZstd: true));
    }

    [Fact]
    public void Registration_PointsEveryCommandAtTheExe_AndCanBeUndone()
    {
        const string exe = @"C:\Programlar\AFU Zip\AfuZip.exe";
        var writes = ShellRegistration.Build(exe);

        // Her komut tırnaklı exe yoluyla başlar ve %1'i tırnak içinde verir (boşluklu yollar).
        var commands = writes.Where(w => w.Key.EndsWith(@"\command", StringComparison.Ordinal)).Select(w => (string)w.Value).ToList();
        Assert.NotEmpty(commands);
        Assert.All(commands, c => Assert.StartsWith($"\"{exe}\"", c));
        Assert.All(commands, c => Assert.EndsWith("\"%1\"", c));
        Assert.Contains(commands, c => c.Contains("--extract-here"));
        Assert.Contains(commands, c => c.Contains("--compress share"));

        // Komut satırı biçimi ayrıştırıcıyla uyumlu.
        var extract = commands.Single(c => c.Contains("--extract-folder"));
        Assert.Equal(ShellVerb.ExtractToFolder, ShellCommand.Parse(CommandLineSplit(extract).Skip(1)).Verb);

        // Her uzantı alt menüye ve "Birlikte aç"a bağlı; zip tabanlı belgeler değil.
        foreach (var extension in new[] { ".7z", ".zip", ".rar", ".tar", ".001" })
        {
            Assert.Contains(writes, w => w.Key.EndsWith($@"SystemFileAssociations\{extension}\shell\AfuZip") && w.Name == "ExtendedSubCommandsKey");
            Assert.Contains(writes, w => w.Key.EndsWith($@"\{extension}\OpenWithProgids") && w.Name == ShellRegistration.ProgId);
        }
        Assert.DoesNotContain(ShellRegistration.Extensions, e => e is ".docx" or ".jar" or ".apk");

        // Kaldırma, yazılan her anahtarı ya bütünüyle siler ya da değerini geri alır.
        var owned = ShellRegistration.OwnedKeys(exe);
        var shared = ShellRegistration.SharedValues();
        foreach (var write in writes)
        {
            var removed = owned.Any(k => write.Key.Equals(k, StringComparison.OrdinalIgnoreCase) ||
                                         write.Key.StartsWith(k + @"\", StringComparison.OrdinalIgnoreCase)) ||
                          shared.Any(v => v.Key == write.Key && v.Name == write.Name);
            Assert.True(removed, $"Kaldırılmıyor: {write.Key} [{write.Name}]");
        }
    }

    private static IEnumerable<string> CommandLineSplit(string commandLine)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ' ' && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            yield return current.ToString();
    }
}

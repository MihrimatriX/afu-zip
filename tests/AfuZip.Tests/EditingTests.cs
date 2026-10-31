namespace AfuZip.Tests;

public class ArchiveUpdatePlannerTests
{
    private static readonly ArchiveEntry[] Entries =
    [
        new(0, "README.md", false, 10),
        new(1, "src", true, 0),
        new(2, "src/Oyuncu.cs", false, 100),
        new(3, "src/alt/Düşman.cs", false, 200),
        new(4, "assets/logo.png", false, 300), // "assets" klasörü örtük (girdisi yok)
    ];

    private static IReadOnlyList<UpdateItem> Plan(ArchiveUpdate update, params SourceItem[] sources) =>
        ArchiveUpdatePlanner.Plan(Entries, update, sources.Length > 0 ? sources : null);

    [Fact]
    public void EmptyUpdate_KeepsEverythingInOrder()
    {
        var plan = Plan(new ArchiveUpdate());
        Assert.All(plan, item => Assert.IsType<UpdateItem.Keep>(item));
        Assert.Equal([0, 1, 2, 3, 4], plan.Cast<UpdateItem.Keep>().Select(k => k.Index));
    }

    [Fact]
    public void DeletingFolder_RemovesEverythingUnderIt_IncludingImplicitFolders()
    {
        var plan = Plan(new ArchiveUpdate { Delete = ["src/", "assets"] });
        Assert.Equal(["README.md"], plan.Select(p => p.Path));
    }

    [Fact]
    public void RenamingFolder_MovesChildren()
    {
        var plan = Plan(new ArchiveUpdate { Rename = new Dictionary<string, string> { ["src"] = "kaynak" } });
        Assert.Equal(["README.md", "kaynak", "kaynak/Oyuncu.cs", "kaynak/alt/Düşman.cs", "assets/logo.png"], plan.Select(p => p.Path));
        Assert.IsType<UpdateItem.Rename>(plan[2]);
        Assert.IsType<UpdateItem.Keep>(plan[0]);
    }

    [Fact]
    public void Rename_RejectsConflictsAndInvalidNames()
    {
        Assert.Throws<IOException>(() => Plan(new ArchiveUpdate { Rename = new Dictionary<string, string> { ["README.md"] = "src" } }));
        Assert.Throws<ArgumentException>(() => Plan(new ArchiveUpdate { Rename = new Dictionary<string, string> { ["README.md"] = "a/b" } }));
        Assert.Throws<ArgumentException>(() => Plan(new ArchiveUpdate { Rename = new Dictionary<string, string> { ["README.md"] = "CON" } }));
        Assert.Throws<FileNotFoundException>(() => Plan(new ArchiveUpdate { Rename = new Dictionary<string, string> { ["yok.txt"] = "x" } }));

        // Yalnızca harf büyüklüğü değişiyorsa izin verilir.
        var plan = Plan(new ArchiveUpdate { Rename = new Dictionary<string, string> { ["README.md"] = "readme.md" } });
        Assert.Equal("readme.md", plan[0].Path);
    }

    [Fact]
    public void Add_GoesToTargetFolder_AndReplacesSamePath()
    {
        var plan = Plan(
            new ArchiveUpdate { AddToFolder = "src" },
            new SourceItem(@"C:\proje\Oyuncu.cs", "Oyuncu.cs", false, 150),
            new SourceItem(@"C:\proje\Yeni.cs", "Yeni.cs", false, 50));

        Assert.Equal(["README.md", "src", "src/alt/Düşman.cs", "assets/logo.png", "src/Oyuncu.cs", "src/Yeni.cs"], plan.Select(p => p.Path));
        Assert.IsType<UpdateItem.Add>(plan[^2]);
    }

    [Fact]
    public void AddingExistingFolder_MergesInsteadOfDuplicating()
    {
        var plan = Plan(
            new ArchiveUpdate(),
            new SourceItem(@"C:\proje\src", "src", true, 0),
            new SourceItem(@"C:\proje\src\Yeni.cs", "src/Yeni.cs", false, 10));

        Assert.Single(plan, p => p.Path == "src");
        Assert.Contains(plan, p => p is UpdateItem.Add { Path: "src/Yeni.cs" });
    }

    [Fact]
    public void Add_ConflictPolicies()
    {
        var source = new SourceItem(@"C:\README.md", "README.md", false, 1);
        Assert.Throws<IOException>(() => Plan(new ArchiveUpdate { OnConflict = ConflictPolicy.Fail }, source));

        var skipped = Plan(new ArchiveUpdate { OnConflict = ConflictPolicy.Skip }, source);
        Assert.IsType<UpdateItem.Keep>(Assert.Single(skipped, p => p.Path == "README.md"));

        Assert.Equal(["README.md"], ArchiveUpdatePlanner.FindConflicts(Entries, [source], ""));
    }

    [Fact]
    public void Add_PrefersExactCaseMatch()
    {
        // Linux'ta oluşturulmuş arşivlerde "A.txt" ve "a.txt" birlikte olabilir: "a.txt" eklemek "A.txt"yi silmemeli.
        ArchiveEntry[] entries = [new(0, "A.txt", false, 1), new(1, "a.txt", false, 1)];
        var plan = ArchiveUpdatePlanner.Plan(entries, new ArchiveUpdate(), [new SourceItem(@"C:\a.txt", "a.txt", false, 2)]);

        Assert.Equal(["A.txt", "a.txt"], plan.Select(p => p.Path));
        Assert.IsType<UpdateItem.Keep>(plan[0]);
        Assert.IsType<UpdateItem.Add>(plan[1]);
    }

    [Fact]
    public void DuplicateSourceNames_BecomeOneEntry()
    {
        var plan = ArchiveUpdatePlanner.Plan([], new ArchiveUpdate(),
        [
            new SourceItem(@"C:\a\x.txt", "x.txt", false, 1),
            new SourceItem(@"D:\b\x.txt", "x.txt", false, 2),
        ]);
        var add = Assert.IsType<UpdateItem.Add>(Assert.Single(plan));
        Assert.Equal(@"D:\b\x.txt", add.Source.FullPath);
    }

    [Fact]
    public void DeletingMissingPath_IsReported() =>
        Assert.Throws<FileNotFoundException>(() => Plan(new ArchiveUpdate { Delete = ["yok/dosya.txt"] }));
}

public class OutputTargetTests
{
    [Fact]
    public void SingleFile_IsWrittenUnderTempNameUntilCommit()
    {
        using var temp = new TempDir();
        var final = temp["yedek.7z"];
        File.WriteAllText(final, "eski arşiv");

        using (var target = new OutputTarget(final))
        {
            target.Stream.Write("yeni"u8);
            Assert.Equal("eski arşiv", File.ReadAllText(final)); // commit'e kadar eskisi bozulmaz
            target.Commit();
        }

        Assert.Equal("yeni", File.ReadAllText(final));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void Abandoned_LeavesNoTempFiles()
    {
        using var temp = new TempDir();
        using (var target = new OutputTarget(temp["yarım.7z"], volumeSize: 64 * 1024))
            target.Stream.Write(new byte[200_000]);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void Volumes_SplitAtSize_AndSupportSeekBackLike7z()
    {
        using var temp = new TempDir();
        var data = ManagedEngineTests.RandomBytes(150_000);
        const int volume = 64 * 1024;

        using (var target = new OutputTarget(temp["parça.7z"], volume))
        {
            target.Stream.Write(new byte[32]); // 7z: başlık yeri
            target.Stream.Write(data);
            target.Stream.Position = 0; // bitince başa dönüp başlığı yazar
            target.Stream.Write("7z-header-------------------------"u8[..32]);
            target.Commit();
            Assert.Equal(3, target.Files.Count);
        }

        Assert.Equal(volume, new FileInfo(temp["parça.7z.001"]).Length);
        Assert.Equal(volume, new FileInfo(temp["parça.7z.002"]).Length);
        var joined = File.ReadAllBytes(temp["parça.7z.001"]).Concat(File.ReadAllBytes(temp["parça.7z.002"]))
            .Concat(File.ReadAllBytes(temp["parça.7z.003"])).ToArray();
        Assert.Equal(32 + data.Length, joined.Length);
        Assert.Equal("7z-header"u8.ToArray(), joined[..9]);
        Assert.Equal(data, joined[32..]);
    }

    [Fact]
    public void Commit_RemovesStaleExtraVolumes()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp["eski.7z.003"], "önceki denemeden kalan");
        using (var target = new OutputTarget(temp["eski.7z"], 64 * 1024))
        {
            target.Stream.Write(new byte[70_000]);
            target.Commit();
        }
        Assert.False(File.Exists(temp["eski.7z.003"]));
        Assert.True(File.Exists(temp["eski.7z.002"]));
    }
}

public class SevenZipPropertiesTests
{
    [Fact]
    public void SevenZip_MapsPresetLikeCommandLine()
    {
        var props = AfuZip.Engine.SevenZip.SevenZipProperties.For("7z", CompressionPreset.Maximum, encrypted: true, encryptHeaders: true, threads: 4);
        Assert.Equal([("x", "9"), ("0", "LZMA2"), ("s", "on"), ("mt", "4"), ("he", "on")], props);
    }

    [Theory]
    [InlineData("9", true)]
    [InlineData("22", true)]
    [InlineData("on", false)]
    [InlineData("LZMA2", false)]
    [InlineData("-1", false)]
    [InlineData("", false)]
    public void NumericValues_AreSentAsNumbers(string value, bool numeric) =>
        Assert.Equal(numeric, AfuZip.Engine.SevenZip.SevenZipProperties.TryParseNumber(value, out _));

    [Fact]
    public void Zip_UsesUtf8NamesAndAes()
    {
        var props = AfuZip.Engine.SevenZip.SevenZipProperties.For("zip", CompressionPreset.Share, encrypted: true, encryptHeaders: false, threads: null);
        Assert.Contains(("cu", "on"), props);
        Assert.Contains(("em", "AES256"), props);
        Assert.Contains(("m", "Deflate"), props);
    }

    [Fact]
    public void HeaderEncryption_OnlyWithPassword()
    {
        var props = AfuZip.Engine.SevenZip.SevenZipProperties.For("7z", CompressionPreset.Balanced, encrypted: false, encryptHeaders: true, threads: null);
        Assert.DoesNotContain(props, p => p.Name == "he");
    }

    [Fact]
    public void Zstd_FallsBackToLzma2()
    {
        var fallback = AfuZip.Engine.SevenZip.SevenZipProperties.Fallback("7z", CompressionPreset.Fast);
        Assert.NotNull(fallback);
        Assert.Equal("LZMA2", fallback.Method);
        Assert.InRange(fallback.Level, 1, 9);
    }

    [Theory]
    [InlineData("yedek.tar.gz", "yedek.tar")]
    [InlineData("yedek.tgz", "yedek.tar")]
    [InlineData("Proje.tar.zst", "Proje.tar")]
    [InlineData("a.b.tar.xz", "a.b.tar")]
    public void InnerTarName(string archive, string tar) =>
        Assert.Equal(tar, AfuZip.Engine.SevenZip.SevenZipWriter.InnerTarName(archive));
}

public class CompressionEstimatorTests
{
    private static readonly SourceItem[] Mixed =
    [
        new(@"C:\p\kaynak", "kaynak", true, 0),
        new(@"C:\p\kaynak\Oyuncu.cs", "kaynak/Oyuncu.cs", false, 10_000_000),
        new(@"C:\p\kaynak\tanıtım.mp4", "kaynak/tanıtım.mp4", false, 90_000_000),
    ];

    [Fact]
    public void AlreadyCompressedContent_DominatesTheResult()
    {
        var estimate = CompressionEstimator.Estimate(Mixed, CompressionPreset.Maximum, threads: 8);
        Assert.Equal(2, estimate.Files);
        Assert.Equal(90_000_000, estimate.AlreadyCompressedBytes);
        Assert.InRange(estimate.Ratio, 0.85, 1.0); // video küçülmez; kazanç yalnızca kodda
    }

    [Fact]
    public void FasterPresets_AreEstimatedFaster_AndStoreIsSizeNeutral()
    {
        var fast = CompressionEstimator.Estimate(Mixed, CompressionPreset.Fast, threads: 8);
        var max = CompressionEstimator.Estimate(Mixed, CompressionPreset.Maximum, threads: 8);
        Assert.True(fast.EstimatedTime < max.EstimatedTime);

        var store = CompressionEstimator.Estimate(Mixed, CompressionPreset.Balanced with { Level = 0 });
        Assert.InRange(store.Ratio, 0.99, 1.01);
    }
}

public class LazyFileStreamTests
{
    [Fact]
    public void ClosesAtEnd_AndReopensAfterSeekBack()
    {
        using var temp = new TempDir();
        var path = temp.WriteFile("veri.bin", ManagedEngineTests.RandomBytes(100_000));
        Exception? fault = null;
        using var stream = new AfuZip.Engine.SevenZip.Interop.LazyFileStream(path, ex => fault = ex);

        Assert.Equal(100_000, stream.Length);
        var first = new MemoryStream();
        stream.CopyTo(first);

        // Sona gelince dosya kapanır: başka bir program onu silebilir/yeniden adlandırabilir.
        File.Move(path, path + ".taşındı");
        File.Move(path + ".taşındı", path);

        // zip "store"a düşünce başa döner ve yeniden okur.
        stream.Position = 0;
        var second = new MemoryStream();
        stream.CopyTo(second);
        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.Null(fault);
    }

    [Fact]
    public void MissingFile_ReportsFaultOnFirstRead()
    {
        Exception? fault = null;
        using var stream = new AfuZip.Engine.SevenZip.Interop.LazyFileStream("/yok/böyle-bir-dosya.bin", ex => fault = ex);
        Assert.ThrowsAny<IOException>(() => stream.ReadByte());
        Assert.Contains("okunamadı", fault?.Message);
    }
}

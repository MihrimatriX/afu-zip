using System.Globalization;

namespace AfuZip.Tests;

public class ArchiveTreeTests
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private static ArchiveEntry File(int i, string path, long size, long? packed = null, int day = 1) =>
        new(i, path, false, size, packed ?? size / 2, new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero));

    private static ArchiveTree Sample() => new(
    [
        File(0, "proje/src/Program.cs", 1000),
        File(1, "proje/src/Yardımcı.cs", 300),
        File(2, "proje/README.md", 50, day: 5),
        new ArchiveEntry(3, "proje/boş/", true, 0),
        File(4, "kök.txt", 10),
        File(5, "proje/src/dosya10.txt", 1),
        File(6, "proje/src/dosya2.txt", 1),
    ], Turkish);

    [Fact]
    public void ImplicitFolders_AreCreatedFromFilePaths()
    {
        var tree = Sample();
        Assert.True(tree.FolderExists("proje/src"));
        Assert.Equal(["proje"], tree.Children("").Where(i => i.IsFolder).Select(i => i.Name));
        Assert.Equal(["boş", "src"], tree.Root.Children.Single().Children.Select(n => n.Name));
    }

    [Fact]
    public void SolidArchive_FolderPackedSizeIsHidden()
    {
        // Solid 7z: bloğun sıkışık boyutu yalnızca ilk dosyada; klasör toplamı "%8284" gibi anlamsız oranlar üretiyordu.
        var tree = new ArchiveTree(
        [
            new ArchiveEntry(0, "assets/logo.svg", false, 1400, 112_000),
            new ArchiveEntry(1, "assets/ikon.png", false, 900, null),
            new ArchiveEntry(2, "README.md", false, 257, null),
        ], Turkish);
        var assets = tree.Children("").Single(i => i.IsFolder);
        Assert.Null(assets.PackedSize);
        Assert.Null(assets.Ratio);
        Assert.Equal(2300, assets.Size);
    }

    [Fact]
    public void FolderStats_AggregateDescendants()
    {
        var tree = Sample();
        var src = tree.StatsOf("proje/src");
        Assert.Equal(4, src.Files);
        Assert.Equal(1302, src.Size);
        Assert.Equal(1352, tree.StatsOf("proje").Size);
        Assert.Equal(1362, tree.StatsOf("").Size);
        Assert.Equal(0, tree.StatsOf("proje/boş").Files);
    }

    [Fact]
    public void Children_FoldersFirstAndNaturalNameOrder()
    {
        var names = Sample().Children("proje/src").Select(i => i.Name).ToList();
        Assert.Equal(["dosya2.txt", "dosya10.txt", "Program.cs", "Yardımcı.cs"], names);
    }

    [Fact]
    public void Children_SortBySizeDescending_KeepsFoldersOnTop()
    {
        var items = Sample().Children("proje", SortColumn.Size, descending: true);
        Assert.True(items[0].IsFolder && items[1].IsFolder);
        Assert.Equal("src", items[0].Name);   // 1302 bayt
        Assert.Equal("boş", items[1].Name);   // 0 bayt
        Assert.Equal("README.md", items[2].Name);
    }

    [Fact]
    public void Search_IsCaseInsensitiveWithTurkishRules()
    {
        var tree = Sample();
        Assert.Equal(["Yardımcı.cs"], tree.Search("YARDIMCI").Select(i => i.Name));
        Assert.Contains(tree.Search("src"), i => i.IsFolder && i.Path == "proje/src");
        Assert.Empty(tree.Search("   "));
    }

    [Fact]
    public void CollectIndices_ExpandsSelectedFolders()
    {
        var tree = Sample();
        var src = tree.Children("proje").Single(i => i.Name == "src");
        var readme = tree.Children("proje").Single(i => i.Name == "README.md");

        Assert.Equal([0, 1, 2, 5, 6], tree.CollectIndices([src, readme]));
        Assert.Equal([3], tree.CollectIndices([tree.Children("proje").Single(i => i.Name == "boş")]));
    }
}

public class SmartExtractTests
{
    private static ArchiveEntry E(string path) => new(0, path, path.EndsWith('/'), 1);

    [Theory]
    [InlineData("yedek.tar.gz", "yedek")]
    [InlineData("Belgeler.ZIP", "Belgeler")]
    [InlineData("film.part1.rar", "film")]
    [InlineData("film.part01.rar", "film")]
    [InlineData("proje.7z.001", "proje")]
    [InlineData("kaynak.tar.zst", "kaynak")]
    [InlineData(".7z", "arşiv")]
    public void ArchiveStem_StripsCompoundAndVolumeExtensions(string fileName, string expected) =>
        Assert.Equal(expected, SmartExtract.ArchiveStem(fileName));

    [Fact]
    public void SingleRootFolder_ExtractsIntoBaseDirectory()
    {
        using var temp = new TempDir();
        var dest = SmartExtract.ResolveDestination("x.zip", [E("proje/"), E("proje/a.txt"), E("./proje/b/c.txt")], temp.Path);
        Assert.Equal(temp.Path, dest);
    }

    [Fact]
    public void MultipleRoots_GetArchiveNamedFolder()
    {
        using var temp = new TempDir();
        var dest = SmartExtract.ResolveDestination("fotoğraflar.zip", [E("a.jpg"), E("b.jpg")], temp.Path);
        Assert.Equal(Path.Combine(temp.Path, "fotoğraflar"), dest);
    }

    [Fact]
    public void ExistingTargets_AreNotMergedInto()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp["proje"]);
        Directory.CreateDirectory(temp["x"]);

        // Tek kök ama "proje" zaten var → arşiv adıyla klasör; o da varsa "x (2)".
        var dest = SmartExtract.ResolveDestination("x.zip", [E("proje/a.txt")], temp.Path);
        Assert.Equal(Path.Combine(temp.Path, "x (2)"), dest);
    }
}

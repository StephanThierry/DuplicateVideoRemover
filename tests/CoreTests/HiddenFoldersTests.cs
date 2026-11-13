using System;
using System.IO;
using deepduplicates;
using Xunit;

namespace CoreTests;

public class HiddenFoldersTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dvr-hidden-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(dir, "output", "hiddenfolders.json");
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Theory]
    [InlineData(@"D:\Videos\Shows", @"D:\Videos\Shows", true)]
    [InlineData(@"D:\Videos\Shows", @"D:\Videos\Shows\Season 1", true)]          // a parent hides what is below it
    [InlineData(@"D:\Videos\Shows\", @"d:\videos\shows\Season 1\Extras", true)]   // trailing separator, other case
    [InlineData(@"D:\Videos\Shows", @"D:\Videos\Shows 2", false)]                // only whole folder names
    [InlineData(@"D:\Videos\Shows\Season 1", @"D:\Videos\Shows", false)]         // not the folder above
    [InlineData(@"\\nas\share\Archive", @"\\nas\share\Archive\2019", true)]
    [InlineData("/mnt/videos/Archive", "/mnt/videos/Archive/2019", true)]
    [InlineData(@"D:\", @"D:\Videos", true)]
    public void AHiddenFolderCoversItselfAndWhatIsBelowIt(string hidden, string folder, bool covers) =>
        Assert.Equal(covers, HiddenFolders.Covers(hidden, folder));

    [Fact]
    public void TheListIsKeptBetweenRuns()
    {
        var a = new HiddenFolders(File);
        int changes = 0;
        a.Changed += () => changes++;
        Assert.True(a.Add(@"  ""D:\Videos\Shows\""  "));   // pasted with quotes and a trailing separator
        Assert.False(a.Add(@"d:\videos\shows"));            // there already
        Assert.True(a.Add(@"D:\Videos\Movies"));
        Assert.Equal(2, changes);
        Assert.True(a.Hides(@"D:\Videos\Shows\Season 2"));

        var b = new HiddenFolders(File);   // after a restart
        Assert.Equal(new[] { @"D:\Videos\Movies", @"D:\Videos\Shows" }, b.List);
        Assert.True(b.Remove(@"D:\Videos\Shows"));
        Assert.False(b.Hides(@"D:\Videos\Shows\Season 2"));

        b.Clear();
        Assert.Empty(new HiddenFolders(File).List);
    }

    [Fact]
    public void ABrokenFileGivesAnEmptyList()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, "not json");
        Assert.Empty(new HiddenFolders(File).List);
    }
}

using System.Linq;
using deepduplicates;
using Xunit;

namespace CoreTests;

public class FileSortingTests
{
    private static System.Collections.Generic.List<VideoEntry> Files() => new()
    {
        new VideoEntry { Id = 1, Path = "c:/v/b.mp4", Bitrate = 2_000_000 },
        new VideoEntry { Id = 2, Path = "c:/v/a.mp4", Bitrate = 500_000 },
        new VideoEntry { Id = 3, Path = "c:/v/c.mp4", Bitrate = 8_000_000 },
        new VideoEntry { Id = 4, Path = "c:/v/d.mp4", Bitrate = 500_000 },
    };

    [Fact]
    public void BitrateCanBeSortedLowestFirst_TiesByPath()
        => Assert.Equal(new[] { 2, 4, 1, 3 }, FileSorting.Apply(Files(), FileSort.BitrateUp).Select(e => e.Id));

    [Fact]
    public void BitrateCanBeSortedHighestFirst_TiesByPath()
        => Assert.Equal(new[] { 3, 1, 2, 4 }, FileSorting.Apply(Files(), FileSort.BitrateDown).Select(e => e.Id));

    [Fact]
    public void BothBitrateChoicesAreInTheDropDown()
        => Assert.Contains(FileSorting.Choices, c => c.Sort == FileSort.BitrateUp && c.Label.StartsWith("Bitrate"));
}

public class SaneBitrateTests
{
    [Fact]
    public void AnOverflowedHeaderValueIsReplacedByTheAverage()      // 476 MB, 41:05, header said 4,294,096 kbps
        => Assert.InRange(VideoEntry.SaneBitrate(4_294_096_000, 476_000_000, 2465), 1_540_000, 1_550_000);

    [Fact]
    public void ThePlaceholderOf104857kbpsIsReplaced()
        => Assert.InRange(VideoEntry.SaneBitrate(104_857_600, 460_000_000, 3318), 1_100_000, 1_120_000);

    [Fact]
    public void ARealisticValueIsKept()
        => Assert.Equal(4_433_000, VideoEntry.SaneBitrate(4_433_000, 447_000_000, 763));   // the video stream is a bit under the average

    [Fact]
    public void WithoutLengthASmallValueIsKept_AnOverflowIsNot()
    {
        Assert.Equal(900_000, VideoEntry.SaneBitrate(900_000, 1000, -1));
        Assert.Equal(0, VideoEntry.SaneBitrate(4_294_096_000, 1000, -1));
    }

    [Fact]
    public void EntriesMadeFromTheDatabaseUseTheSaneValue()
        => Assert.InRange(VideoEntry.From(new VideoInfo { id = 1, path = "c:/a.wmv", fileSize = 476_000_000, duration = 2465, bitrate = 4_294_096_000 }).Bitrate, 1_540_000, 1_550_000);
}

public class OrderAnyListTests
{
    private record Item(string Path, long Size, double Kbps);
    private static readonly Item[] Items =
    {
        new("c:/b/zeta.mp4", 300, 1000), new("c:/a/alpha.mp4", 100, 3000), new("c:/c/mid.mp4", 200, 2000),
    };
    private static string[] Names(FileSort s) =>
        FileSorting.Order(Items, s, i => i.Path, i => i.Size, i => i.Kbps).Select(i => System.IO.Path.GetFileName(i.Path)).ToArray();

    [Fact] public void BitrateHighestFirst_IsTheOldRe_encodeOrder() => Assert.Equal(new[] { "alpha.mp4", "mid.mp4", "zeta.mp4" }, Names(FileSort.BitrateDown));
    [Fact] public void BitrateLowestFirst() => Assert.Equal(new[] { "zeta.mp4", "mid.mp4", "alpha.mp4" }, Names(FileSort.BitrateUp));
    [Fact] public void SizeLargestFirst() => Assert.Equal(new[] { "zeta.mp4", "mid.mp4", "alpha.mp4" }, Names(FileSort.SizeDown));
    [Fact] public void FilenameZToA() => Assert.Equal(new[] { "zeta.mp4", "mid.mp4", "alpha.mp4" }, Names(FileSort.NameDown));
    [Fact] public void FolderAToZ() => Assert.Equal(new[] { "alpha.mp4", "zeta.mp4", "mid.mp4" }, Names(FileSort.FolderUp));
    [Fact] public void PathIsByTheFullPath() => Assert.Equal(new[] { "alpha.mp4", "zeta.mp4", "mid.mp4" }, Names(FileSort.Path));
}

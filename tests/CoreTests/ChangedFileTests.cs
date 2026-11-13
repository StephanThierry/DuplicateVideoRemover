using System;
using System.IO;
using System.Linq;
using deepduplicates;
using Xunit;

namespace CoreTests;

public class ChangedFileTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dvr-changed-" + Guid.NewGuid().ToString("N"));
    public ChangedFileTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void ARowIsStaleWhenTheFileOnDiskIsNotTheSizeThatWasRead()
    {
        var row = new VideoInfo { fileSize = 4_821_929 };
        Assert.True(VideoHandler.IsStale(row, 260_561_115));      // an unfinished copy that was finished (or encoded again)
        Assert.False(VideoHandler.IsStale(row, 4_821_929));       // the same file
        Assert.False(VideoHandler.IsStale(row, null));            // size unknown: no reason to read again
        Assert.False(VideoHandler.IsStale(new VideoInfo { fileSize = null }, 100));   // never read: it is read anyway
        Assert.False(VideoHandler.IsStale(new VideoInfo { fileSize = -1 }, 100));
        Assert.False(VideoHandler.IsStale(new VideoInfo { fileSize = 0 }, 100));
        Assert.False(VideoHandler.IsStale(row, 0));
    }

    [Fact]
    public void ARowThatIsReadAgain_ForgetsTheOldFilesPicturesAndValues()
    {
        var files = new FileHandler(new Settings { contentFolders = new[] { dir }, excludePaths = new string[0] });
        var handler = new VideoHandler(files, new ImageHandler());
        var row = new VideoInfo
        {
            id = 987654321, duration = 47, fileSize = 4_821_929, bitrate = 736314, reason = "x", formatNotSupported = true,
            image1Checksum = new rgb[1], image2Checksum = new rgb[1], image3Checksum = new rgb[1],
            image1hash_blob = new byte[1], image2hash_blob = new byte[1], image3hash_blob = new byte[1],
            image1hash = new(), image2hash = new(), image3hash = new(),
        };
        // The pictures are kept by row id and reused when they exist: the old file's must go, or the new file would get them
        var shots = Enumerable.Range(1, 3).Select(n => files.screenshotPath(row, n)).ToList();
        foreach (var shot in shots) File.WriteAllBytes(shot, new byte[] { 1 });

        handler.ForgetWhatWasRead(row);

        Assert.All(shots, shot => Assert.False(File.Exists(shot)));
        Assert.Equal(-1, row.duration);                       // read again, as a row whose reading failed is
        Assert.Null(row.image1Checksum); Assert.Null(row.image2Checksum); Assert.Null(row.image3Checksum);
        Assert.Null(row.image1hash_blob); Assert.Null(row.image2hash_blob); Assert.Null(row.image3hash_blob);
        Assert.Null(row.image1hash);
        Assert.False(row.formatNotSupported);
        Assert.Null(row.reason);
        Assert.Equal(4_821_929, row.fileSize);                // replaced by the real size when it is read
        Assert.Equal(987654321, row.id);                      // the same row, so nothing else changes
    }

    [Fact]
    public void TheListingOfTheFolders_ReportsEachFilesSize()
    {
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllBytes(Path.Combine(dir, "a.mp4"), new byte[10]);
        File.WriteAllBytes(Path.Combine(dir, "sub", "b.MKV"), new byte[2500]);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a video");

        var handler = new FileHandler(new Settings { contentFolders = new[] { dir }, excludePaths = new string[0] });
        handler.FindFiles(null, default);

        Assert.Equal(2, handler.allFilesCount);
        Assert.Equal(new[] { Path.Combine(dir, "a.mp4"), Path.Combine(dir, "sub", "b.MKV") }, handler.allFiles.ToArray());
        Assert.Equal(10, handler.allFileSizes[Path.Combine(dir, "a.mp4")]);
        Assert.Equal(2500, handler.allFileSizes[Path.Combine(dir, "sub", "b.MKV")]);
        Assert.False(handler.allFileSizes.ContainsKey(Path.Combine(dir, "notes.txt")));

        // The size is the one the row would be compared with: a file that grew since it was read is stale
        File.WriteAllBytes(Path.Combine(dir, "a.mp4"), new byte[5000]);
        handler.FindFiles(null, default);
        Assert.True(VideoHandler.IsStale(new VideoInfo { fileSize = 10 }, handler.allFileSizes[Path.Combine(dir, "a.mp4")]));

        // ... and the listing knows when each file was written, without opening it
        var written = new DateTime(2026, 11, 6, 22, 35, 14, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(dir, "a.mp4"), written);
        handler.FindFiles(null, default);
        Assert.Equal(written, handler.allFileTimes[Path.Combine(dir, "a.mp4")]);
    }

    [Fact]
    public void AFileWrittenAfterItsPicturesWereMade_IsReadAgain()
    {
        var made = new DateTime(2026, 11, 5, 19, 57, 10, DateTimeKind.Utc);
        // A download that reserved the whole file: same size, finished a day after the scan read it
        Assert.True(VideoHandler.WrittenAfterPictures(made.AddDays(1), made));
        Assert.False(VideoHandler.WrittenAfterPictures(made.AddHours(-3), made));     // written before: the pictures are of the real file
        Assert.False(VideoHandler.WrittenAfterPictures(made.AddSeconds(40), made));   // clocks that differ a little
        Assert.False(VideoHandler.WrittenAfterPictures(null, made));
        Assert.False(VideoHandler.WrittenAfterPictures(made, null));                   // no pictures: it is read anyway
    }

    [Fact]
    public void PicturesLeftOverFromAFailedRead_AreRecognised()
    {
        const string failed = "Screenshot failed! ConversionException. Most likely caused by an invalid or incomplete video.";
        Assert.True(VideoHandler.HasPicturesFromFailedRead(new VideoInfo { reason = failed, image1Checksum = new rgb[1] }));
        Assert.False(VideoHandler.HasPicturesFromFailedRead(new VideoInfo { reason = failed }));   // failed, no pictures: read again anyway
        Assert.False(VideoHandler.HasPicturesFromFailedRead(new VideoInfo { reason = "Too short", image1Checksum = new rgb[1] }));
        Assert.False(VideoHandler.HasPicturesFromFailedRead(new VideoInfo { reason = failed, image1Checksum = new rgb[1], formatNotSupported = true }));
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using deepduplicates;
using SkiaSharp;
using Xunit;

namespace CoreTests;

public class VideoShapeTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dvr-shape-" + Guid.NewGuid().ToString("N"));
    public VideoShapeTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Theory]
    [InlineData(new[] { "width=1920", "height=1080", "sample_aspect_ratio=1:1" }, 1.7778)]
    [InlineData(new[] { "width=1920", "height=1080", "sample_aspect_ratio=1:1", "rotation=-90" }, 0.5625)]   // phone held upright
    [InlineData(new[] { "width=1920", "height=1080", "TAG:rotate=270" }, 0.5625)]                          // older files: a tag
    [InlineData(new[] { "width=1920", "height=1080", "rotation=180" }, 1.7778)]                            // upside down: same shape
    [InlineData(new[] { "width=720", "height=576", "sample_aspect_ratio=16:15" }, 1.3333)]                 // PAL DVD, 4:3
    [InlineData(new[] { "width=720", "height=576", "sample_aspect_ratio=64:45" }, 1.7778)]                 // PAL DVD, 16:9
    [InlineData(new[] { "width=640", "height=480", "sample_aspect_ratio=0:1" }, 1.3333)]                   // not set: square pixels
    [InlineData(new[] { "width=640", "height=480", "sample_aspect_ratio=N/A" }, 1.3333)]
    [InlineData(new[] { "width=N/A", "height=480" }, 0)]
    [InlineData(new string[0], 0)]
    public void Parse_GivesTheShapeAsPlayed(string[] lines, double expected) =>
        Assert.Equal(expected, VideoShape.Parse(lines), 3);

    [Fact]
    public void Fit_CentresThePictureAsLargeAsItFits()
    {
        Assert.Equal((0, 12.5, 240, 135), VideoShape.Fit(16.0 / 9, 240, 160));   // 16:9: a band above and below
        Assert.Equal((75, 0, 90, 160), VideoShape.Fit(9.0 / 16, 240, 160));     // upright: room left and right
        Assert.Equal((0, 0, 240, 160), VideoShape.Fit(0, 240, 160));            // unknown: the whole frame
        Assert.True(VideoShape.FitsAsIs(1.5) && VideoShape.FitsAsIs(1.52) && VideoShape.FitsAsIs(0) && !VideoShape.FitsAsIs(16.0 / 9));
    }

    private static byte[] Shot(SKColor color)
    {
        using var bitmap = new SKBitmap(240, 160);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public void FitPng_DrawsTheScreenshotAtItsShape_TheRestEmpty()
    {
        byte[] png = Shot(SKColors.Red);
        using var upright = SKBitmap.Decode(VideoShape.FitPng(png, 9.0 / 16));
        Assert.Equal((240, 160), (upright.Width, upright.Height));               // same frame: the layout does not move
        Assert.Equal(0, upright.GetPixel(10, 80).Alpha);                         // left of the picture: empty
        Assert.Equal(SKColors.Red, upright.GetPixel(120, 80));                   // the picture, in the middle
        Assert.Equal(0, upright.GetPixel(230, 80).Alpha);

        using var wide = SKBitmap.Decode(VideoShape.FitPng(png, 16.0 / 9));
        Assert.Equal(0, wide.GetPixel(120, 5).Alpha);                            // a band above
        Assert.Equal(SKColors.Red, wide.GetPixel(120, 80));

        Assert.Same(png, VideoShape.FitPng(png, 1.5));                           // already 3:2: as it is
        Assert.Same(png, VideoShape.FitPng(png, VideoShape.Unknown));
        byte[] junk = { 1, 2, 3 };
        Assert.Same(junk, VideoShape.FitPng(junk, 0.5));                         // not a picture: as it is
    }

    private static string? FindFFmpeg()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "ffmpeg", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void Run(string ffmpeg, params string[] args)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public async Task Probe_ReadsTheShapeOfRealFiles()
    {
        string? ffmpeg = FindFFmpeg();
        if (ffmpeg == null) return;
        string wide = Path.Combine(dir, "wide.mp4"), upright = Path.Combine(dir, "upright.mp4"), dvd = Path.Combine(dir, "dvd.mkv");
        Run(ffmpeg, "-y", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=25", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", wide);
        Run(ffmpeg, "-y", "-display_rotation", "90", "-i", wide, "-c", "copy", upright);             // as a phone stores it
        Run(ffmpeg, "-y", "-f", "lavfi", "-i", "testsrc=size=720x576:rate=25", "-t", "1", "-vf", "setsar=64/45", "-c:v", "libx264", "-pix_fmt", "yuv420p", dvd);

        Assert.Equal(16.0 / 9, await VideoShape.ProbeAsync(ffmpeg, wide), 3);
        Assert.Equal(9.0 / 16, await VideoShape.ProbeAsync(ffmpeg, upright), 3);
        Assert.Equal(16.0 / 9, await VideoShape.ProbeAsync(ffmpeg, dvd), 3);
        Assert.Equal(VideoShape.Unknown, await VideoShape.ProbeAsync(ffmpeg, Path.Combine(dir, "missing.mp4")));

        // The screenshot of the upright video is upright too (FFmpeg turns it), so the shape fits it
        string shot = Path.Combine(dir, "upright.png");
        await Picture.TakeAsync(ffmpeg, upright, 0, shot, 240, 160);
        using var fitted = SKBitmap.Decode(VideoShape.ReadFitted(shot, await VideoShape.ProbeAsync(ffmpeg, upright)));
        Assert.Equal(0, fitted.GetPixel(20, 80).Alpha);
        Assert.Equal(255, fitted.GetPixel(120, 80).Alpha);
    }
}

public class VideoShapeColumnTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dvr-shapedb-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void ADatabaseFromBeforeShapesGetsTheColumn_AndKeepsItsVideos()
    {
        Directory.CreateDirectory(dir);
        DataLocation.SetDatabaseFolder(dir);
        try
        {
            // The table as older versions made it, with one video
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DataLocation.DatabasePath}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"CREATE TABLE VideoInfos (id INTEGER NOT NULL CONSTRAINT PK_VideoInfos PRIMARY KEY AUTOINCREMENT, duration INTEGER NULL,
                    fileSize INTEGER NULL, bitrate REAL NULL, fileHash TEXT NULL, image1Checksum_json TEXT NULL, image2Checksum_json TEXT NULL,
                    image3Checksum_json TEXT NULL, path TEXT NULL, formatNotSupported INTEGER NOT NULL, reason TEXT NULL, remove INTEGER NULL,
                    image1hash_blob BLOB NULL, image2hash_blob BLOB NULL, image3hash_blob BLOB NULL);
                    INSERT INTO VideoInfos (duration, path, formatNotSupported) VALUES (60, 'C:\v\a.mp4', 0);";
                cmd.ExecuteNonQuery();
            }
            VideoInfoContext.Prepare();
            VideoInfoContext.Prepare();   // once is enough: the second time changes nothing

            using (var db = new VideoInfoContext())
            {
                var v = Assert.Single(db.VideoInfos);
                Assert.Null(v.aspect);   // not read yet
                v.aspect = 0.5625;
                db.SaveChanges();
            }
            using (var db = new VideoInfoContext()) Assert.Equal(0.5625, Assert.Single(db.VideoInfos).aspect);
        }
        finally
        {
            typeof(DataLocation).GetField("databaseFolder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);
        }
    }
}

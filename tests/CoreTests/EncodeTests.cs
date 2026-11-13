using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using deepduplicates;
using Xunit;

namespace CoreTests;

public class ProgressParserTests
{
    private static FfmpegProgressParser Feed(params string[] lines)
    {
        var p = new FfmpegProgressParser();
        foreach (var l in lines) p.Feed(l);
        return p;
    }

    [Fact]
    public void ReadsABlock()
    {
        var p = new FfmpegProgressParser();
        Assert.False(p.Feed("frame=120"));
        Assert.False(p.Feed("fps=24.50"));
        Assert.False(p.Feed("total_size=1048576"));
        Assert.False(p.Feed("out_time_us=5000000"));
        Assert.False(p.Feed("speed=1.75x"));
        Assert.True(p.Feed("progress=continue"));
        Assert.Equal(TimeSpan.FromSeconds(5), p.OutTime);
        Assert.Equal(24.5, p.Fps);
        Assert.Equal(1.75, p.Speed);
        Assert.Equal(1048576, p.TotalSize);
        Assert.False(p.Ended);
        Assert.Equal(0.05, p.FractionOf(100), 3);
    }

    [Fact]
    public void EarlyBlocksWithNotAvailableValuesDoNotBreak()
    {
        var p = new FfmpegProgressParser();
        foreach (var l in new[] { "fps=0.00", "total_size=N/A", "out_time_us=N/A", "out_time=N/A", "speed=N/A" }) p.Feed(l);
        Assert.True(p.Feed("progress=continue"));
        Assert.Null(p.OutTime);
        Assert.Equal(0, p.Speed);
        Assert.Equal(0, p.FractionOf(100));
    }

    [Fact]
    public void FallsBackToTheTimeText_AndNegativeTimeIsZero()
    {
        Assert.Equal(TimeSpan.FromSeconds(62.5), Feed("out_time=00:01:02.500000").OutTime);
        Assert.Equal(TimeSpan.Zero, Feed("out_time=-00:00:00.033000").OutTime);
        Assert.Equal(TimeSpan.Zero, Feed("out_time_us=-33000").OutTime);
    }

    [Fact]
    public void FractionIsNotOneUntilFFmpegEnds()
    {
        var p = Feed("out_time_us=200000000", "progress=continue");
        Assert.Equal(0.999, p.FractionOf(100), 3);
        p.Feed("progress=end");
        Assert.True(p.Ended);
        Assert.Equal(1, p.FractionOf(100));
        Assert.Equal(0, p.FractionOf(0));
    }
}

public class ArgumentsTests
{
    [Fact]
    public void ScriptArgumentsAreUnchanged()
    {
        var e = EncodeSettings.CreateDefault();
        var args = EncodePlanner.Arguments("/v/a.avi", e);
        Assert.Equal("-n", args[0]);
        Assert.DoesNotContain("-progress", args);
        Assert.Equal(EncodePlanner.OutputPath("/v/a.avi", e), args[^1]);
    }

    [Theory]
    [InlineData("mp4", "mp4")]
    [InlineData("mkv", "matroska")]
    public void PartArgumentsReportProgressAndNameTheFormat(string container, string format)
    {
        var e = EncodeSettings.CreateDefault();
        e.container = container;
        var args = EncodePlanner.Arguments("/v/a.avi", e, "/v/a_x.part");
        Assert.Equal("/v/a_x.part", args[^1]);
        Assert.Equal(format, args[^2]);
        Assert.Equal("-f", args[^3]);
        Assert.Contains("-y", args);          // the engine has decided about an existing result itself
        Assert.DoesNotContain("-n", args);
        int p = args.IndexOf("-progress");
        Assert.Equal("pipe:1", args[p + 1]);
        Assert.Contains("-nostdin", args);
    }
}

public class EncodeEngineTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dvr-enc-" + Guid.NewGuid().ToString("N"));
    private readonly string state;
    private readonly List<EncodeEngine> engines = new();

    public EncodeEngineTests()
    {
        Directory.CreateDirectory(dir);
        state = Path.Combine(dir, "state", "encodequeue.json");
    }

    public void Dispose()
    {
        foreach (var e in engines) e.Shutdown();
        try { Directory.Delete(dir, true); } catch { }
    }

    private sealed class FakeRunner : IEncodeRunner
    {
        public readonly List<string> Inputs = new();
        public Func<IReadOnlyList<string>, Action<string>, Action<string>, CancellationToken, Task<int>>? Behavior;

        // What the check of a new file finds (ffprobe's length, and which second a picture cannot be made at)
        public double ProbeSeconds = 100;
        public int ProbeExit;
        public int? FailPictureAt;
        public readonly List<int> PictureSeconds = new();

        public Task<int> RunAsync(string program, IReadOnlyList<string> args, Action<string> onOutput, Action<string> onError, CancellationToken ct)
        {
            if (Path.GetFileName(program).StartsWith("ffprobe"))
            {
                if (ProbeExit != 0) { onError("moov atom not found"); return Task.FromResult(ProbeExit); }
                onOutput("codec_type=video");
                onOutput("duration=" + ProbeSeconds.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture));
                return Task.FromResult(0);
            }
            if (args.Contains("-frames:v"))
            {
                // The second the picture is for: decoding starts at -ss, the select filter picks the frame from there
                var list = args.ToList();
                double from = list.Contains("-ss") ? double.Parse(list[list.IndexOf("-ss") + 1], System.Globalization.CultureInfo.InvariantCulture) : 0;
                var gte = System.Text.RegularExpressions.Regex.Match(list[list.IndexOf("-vf") + 1], @"gte\(t\\,([0-9.]+)\)");
                int second = (int)Math.Round(from + double.Parse(gte.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                lock (PictureSeconds) PictureSeconds.Add(second);
                if (second == FailPictureAt) { onError("Invalid data found when processing input"); return Task.FromResult(1); }
                using var bitmap = new SkiaSharp.SKBitmap(240, 160);
                bitmap.Erase(SkiaSharp.SKColors.Gray);
                using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(args[^1], data.ToArray());
                return Task.FromResult(0);
            }
            lock (Inputs) Inputs.Add(args[args.ToList().IndexOf("-i") + 1]);
            return (Behavior ?? Succeed)(args, onOutput, onError, ct);
        }

        public static Task<int> Succeed(IReadOnlyList<string> args, Action<string> onOut, Action<string> onErr, CancellationToken ct)
        {
            File.WriteAllText(args[^1], new string('x', 500));
            onOut("out_time_us=50000000"); onOut("speed=2.0x"); onOut("progress=continue");
            onOut("out_time_us=100000000"); onOut("progress=end");
            return Task.FromResult(0);
        }

        /// <summary>Writes a partial file and then runs until cancelled.</summary>
        public static async Task<int> Hang(IReadOnlyList<string> args, Action<string> onOut, Action<string> onErr, CancellationToken ct)
        {
            File.WriteAllText(args[^1], "partial");
            onOut("out_time_us=25000000"); onOut("speed=1.0x"); onOut("progress=continue");
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    private EncodeEngine NewEngine(FakeRunner runner)
    {
        var e = new EncodeEngine(state, () => Task.FromResult("ffmpeg"), runner) { ProgramName = "the tests" };
        engines.Add(e);
        return e;
    }

    private EncodeCandidate Video(string name, int id = 1)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, new string('v', 4000));
        return new EncodeCandidate { Id = id, Path = path, SizeBytes = 4000, DurationSeconds = 100 };
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Timed out waiting for " + what);
            Thread.Sleep(20);
        }
    }

    private static string Part(EncodeItemInfo i) => EncodePlanner.PartPath(i.OutputPath);

    [Fact]
    public void EncodesTheQueueInOrderAndRenamesThePartFile()
    {
        var runner = new FakeRunner();
        var engine = NewEngine(runner);
        var a = Video("a.mp4", 1); var b = Video("b.mp4", 2);
        var result = engine.Enqueue(new[] { a, b }, EncodeSettings.CreateDefault());
        Assert.Equal(2, result.Added);
        WaitFor(() => engine.Current.DoneCount == 2 && !engine.Current.Running, "both done");

        Assert.Equal(new[] { a.Path, b.Path }, runner.Inputs);
        foreach (var i in engine.Current.Items)
        {
            Assert.Equal(EncodeItemStatus.Done, i.Status);
            Assert.True(File.Exists(i.OutputPath));
            Assert.False(File.Exists(Part(i)));
            Assert.Equal(500, i.OutputBytes);
            Assert.Equal(3500, i.Saved);
            // The new file was checked like the scan checks a video: its three pictures are kept for the interface
            Assert.True(i.CheckOk);
            Assert.True(i.CanDeleteOriginal);
            Assert.True(i.HasCheckShots);
            for (int n = 1; n <= 3; n++) Assert.True(File.Exists(engine.CheckShotPath(i.Id, n)));
        }
        // Two videos, each with pictures at 25%, 50% and 75% of its length (and one of the last seconds, 99)
        Assert.Equal(new[] { 25, 25, 50, 50, 75, 75 }, runner.PictureSeconds.Where(x => x != 99).OrderBy(x => x).ToArray());
        Assert.Equal(1, engine.Current.Fraction);
        Assert.Equal(7000, engine.Current.SavedBytes);
        Assert.Contains(engine.Current.Log, l => l.Contains("Finished a.mp4"));
    }

    [Fact]
    public void AddingTheSameVideoTwiceIsIgnored()
    {
        var runner = new FakeRunner { Behavior = FakeRunner.Hang };
        var engine = NewEngine(runner);
        var a = Video("a.mp4");
        Assert.Equal(1, engine.Enqueue(new[] { a }, EncodeSettings.CreateDefault()).Added);
        var again = engine.Enqueue(new[] { a }, EncodeSettings.CreateDefault());
        Assert.Equal(0, again.Added);
        Assert.Equal(1, again.AlreadyThere);
        Assert.Single(engine.Current.Items);
    }

    [Fact]
    public void VideosAddedWhileRunningJoinTheQueue()
    {
        var gate = new SemaphoreSlim(0);
        var runner = new FakeRunner
        {
            Behavior = async (args, o, e, ct) => { await gate.WaitAsync(ct); return await FakeRunner.Succeed(args, o, e, ct); }
        };
        var engine = NewEngine(runner);
        engine.Enqueue(new[] { Video("a.mp4", 1) }, EncodeSettings.CreateDefault());
        WaitFor(() => engine.Current.Current != null, "first video running");
        engine.Enqueue(new[] { Video("b.mp4", 2) }, EncodeSettings.CreateDefault());
        Assert.Equal(1, engine.Current.QueuedCount);
        gate.Release(2);
        WaitFor(() => engine.Current.DoneCount == 2, "both done");
    }

    [Fact]
    public void AFailedEncodeIsReportedAndItsPartFileDeleted()
    {
        var runner = new FakeRunner
        {
            Behavior = (args, o, err, ct) => { File.WriteAllText(args[^1], "junk"); err("Error opening input file"); return Task.FromResult(1); }
        };
        var engine = NewEngine(runner);
        var a = Video("a.mp4"); var b = Video("b.mp4", 2);
        engine.Enqueue(new[] { a, b }, EncodeSettings.CreateDefault());
        WaitFor(() => engine.Current.FailedCount == 2, "both failed");
        var item = engine.Current.Items[0];
        Assert.Equal("FFmpeg could not encode it: Error opening input file", item.Message);
        Assert.False(File.Exists(Part(item)));
        Assert.False(File.Exists(item.OutputPath));
    }

    [Fact]
    public void ExistingResultThatChecksOutIsReportedAsAlreadyEncoded()
    {
        var runner = new FakeRunner();
        var engine = NewEngine(runner);
        var a = Video("a.mp4");
        var settings = EncodeSettings.CreateDefault();
        File.WriteAllText(EncodePlanner.OutputPath(a.Path, settings), "earlier result");
        engine.Enqueue(new[] { a }, settings);
        WaitFor(() => engine.Current.AlreadyEncodedCount == 1, "already encoded");
        Assert.Empty(runner.Inputs);                                                       // nothing was encoded
        Assert.Equal("earlier result", File.ReadAllText(EncodePlanner.OutputPath(a.Path, settings)));   // and nothing overwritten
        var item = engine.Current.Items[0];
        Assert.Equal(EncodeItemStatus.AlreadyEncoded, item.Status);
        Assert.Equal("earlier result".Length, item.OutputBytes);
        Assert.True(item.CheckOk && item.CanDeleteOriginal && item.HasCheckShots);
        Assert.Contains("Same length", item.Check);
        for (int n = 1; n <= 3; n++) Assert.True(File.Exists(engine.CheckShotPath(item.Id, n)));
        // The pictures are at the scan's points: 25%, 50% and 75% of the original's length, plus the last seconds
        Assert.Equal(new[] { 25, 50, 75, 99 }, runner.PictureSeconds.OrderBy(x => x).ToArray());
        Assert.Equal(EncodeItemStatus.AlreadyEncoded, engine.Current.StatusByPath()[a.Path]);   // greyed out in the Re-encode list
    }

    [Fact]
    public void ExistingResultThatIsTooShortIsIncomplete()
    {
        var runner = new FakeRunner { ProbeSeconds = 49 };
        var engine = NewEngine(runner);
        var a = Video("a.mp4");
        var settings = EncodeSettings.CreateDefault();
        File.WriteAllText(EncodePlanner.OutputPath(a.Path, settings), "cut off");
        engine.Enqueue(new[] { a }, settings);
        WaitFor(() => engine.Current.IncompleteCount == 1, "incomplete");
        var item = engine.Current.Items[0];
        Assert.Contains("0:49 long, the original is 1:40", item.Message);
        Assert.True(item.CanReplace);
        Assert.False(item.CanDeleteOriginal);
        Assert.Empty(runner.PictureSeconds);                      // no point in making pictures of it
        Assert.True(File.Exists(EncodePlanner.OutputPath(a.Path, settings)));   // never deleted by the engine
        Assert.False(engine.Current.StatusByPath().ContainsKey(a.Path));        // not greyed out: it can be encoded again
    }

    [Fact]
    public void ExistingResultThatCannotBeReadIsIncomplete()
    {
        var engine = NewEngine(new FakeRunner { ProbeExit = 1 });
        var a = Video("a.mp4");
        var settings = EncodeSettings.CreateDefault();
        File.WriteAllText(EncodePlanner.OutputPath(a.Path, settings), "junk");
        engine.Enqueue(new[] { a }, settings);
        WaitFor(() => engine.Current.IncompleteCount == 1, "incomplete");
        Assert.Contains("cannot be read (moov atom not found)", engine.Current.Items[0].Message);
    }

    [Theory]
    [InlineData(75, "75%")]
    [InlineData(99, "the end")]
    public void AFileWhosePictureCannotBeMadeIsIncompleteEvenWithTheRightLength(int second, string where)
    {
        // A cut-off file can still say it has its full length: only reading it shows that
        var engine = NewEngine(new FakeRunner { FailPictureAt = second });
        var a = Video("a.mp4");
        var settings = EncodeSettings.CreateDefault();
        File.WriteAllText(EncodePlanner.OutputPath(a.Path, settings), "header only");
        engine.Enqueue(new[] { a }, settings);
        WaitFor(() => engine.Current.IncompleteCount == 1, "incomplete");
        var item = engine.Current.Items[0];
        Assert.Contains($"the picture at {where} could not be made (Invalid data found when processing input)", item.Message);
        Assert.False(item.CheckOk);
    }

    [Fact]
    public void AnIncompleteNewFileCanBeReplacedByEncodingAgain()
    {
        var runner = new FakeRunner { ProbeSeconds = 5 };
        var engine = NewEngine(runner);
        var a = Video("a.mp4");
        var settings = EncodeSettings.CreateDefault();
        string output = EncodePlanner.OutputPath(a.Path, settings);
        File.WriteAllText(output, "cut off");
        engine.Enqueue(new[] { a }, settings);
        WaitFor(() => engine.Current.IncompleteCount == 1 && !engine.Current.Running, "incomplete");
        var id = engine.Current.Items[0].Id;

        Assert.False(engine.Retry("no-such-id"));
        File.Delete(output);                 // (the app moves it to the Recycle Bin first)
        runner.ProbeSeconds = 100;
        Assert.True(engine.Retry(id));
        WaitFor(() => engine.Current.DoneCount == 1 && !engine.Current.Running, "encoded again");
        Assert.True(engine.Current.Items.Single().CheckOk);
        Assert.Single(engine.Current.Items);
    }

    [Fact]
    public void TheCheckIsKeptAcrossARestartAndItsPicturesGoWithTheItem()
    {
        var engine = NewEngine(new FakeRunner());
        var a = Video("a.mp4");
        var settings = EncodeSettings.CreateDefault();
        File.WriteAllText(EncodePlanner.OutputPath(a.Path, settings), "earlier result");
        engine.Enqueue(new[] { a }, settings);
        WaitFor(() => engine.Current.AlreadyEncodedCount == 1 && !engine.Current.Running, "checked");
        var id = engine.Current.Items[0].Id;

        var again = NewEngine(new FakeRunner());
        var item = again.Current.Items.Single();
        Assert.True(item.CheckOk && item.HasCheckShots && item.CanDeleteOriginal);
        Assert.Contains("Same length", item.Check);
        Assert.True(File.Exists(again.CheckShotPath(id, 1)));

        Assert.True(again.MarkOriginalDeleted(id));
        Assert.True(again.Current.Items.Single().OriginalDeleted);
        Assert.False(again.Current.Items.Single().CanDeleteOriginal);     // only once

        again.ClearFinished();
        Assert.False(File.Exists(again.CheckShotPath(id, 1)));            // pictures of a cleared item are removed
    }

    [Fact]
    public void TheTwoCopiesAreDeletedIndependently_EachKeepsItsButtonsWhileItExists()
    {
        var engine = NewEngine(new FakeRunner());
        var a = Video("a.mp4"); var b = Video("b.mp4", 2);
        engine.Enqueue(new[] { a, b }, EncodeSettings.CreateDefault());
        WaitFor(() => engine.Current.DoneCount == 2 && !engine.Current.Running, "done");
        var first = engine.Current.Items[0]; var second = engine.Current.Items[1];
        Assert.True(first.CanDeleteNew && first.CanDeleteOriginal);

        // The original of the second is deleted: only that copy loses its buttons, the new file keeps its delete button
        Assert.True(engine.MarkOriginalDeleted(second.Id));
        var s2 = engine.Find(second.Id)!;
        Assert.False(s2.CanDeleteOriginal);
        Assert.True(s2.CanDeleteNew);

        // ...and then the last copy can be deleted too: nothing is left to delete
        Assert.True(engine.MarkNewDeleted(second.Id));
        s2 = engine.Find(second.Id)!;
        Assert.False(s2.CanDeleteOriginal || s2.CanDeleteNew);
        Assert.Equal(2, engine.Current.Items.Count);                   // the rows stay, greyed out

        // The new file of the first is deleted: the original keeps its delete button, and the video is planned again like any other
        Assert.True(engine.MarkNewDeleted(first.Id));
        var f1 = engine.Find(first.Id)!;
        Assert.False(f1.CanDeleteNew);
        Assert.True(f1.CanDeleteOriginal);
        Assert.False(engine.Current.StatusByPath().ContainsKey(a.Path));
        Assert.Contains("The new file has been deleted", EncodeText.Status(f1));
        Assert.Equal(0, engine.Current.SavedBytes);                    // nothing saved by files that are gone
        Assert.Equal(1, engine.Enqueue(new[] { a }, EncodeSettings.CreateDefault()).Added);   // can be queued again (replaces the row)
    }

    [Fact]
    public async Task FinishedVideosWhoseFilesAreGone_AreMarkedDeleted_AsTheCheckGoesOn()
    {
        var engine = NewEngine(new FakeRunner());
        var a = Video("a.mp4"); var b = Video("b.mp4", 2); var c = Video("c.mp4", 3);
        var settings = EncodeSettings.CreateDefault();
        engine.Enqueue(new[] { a, b, c }, settings);
        WaitFor(() => engine.Current.DoneCount == 3 && !engine.Current.Running, "done");

        // Outside the program: a's original, b's new file, and both files of c are deleted
        File.Delete(a.Path);
        File.Delete(EncodePlanner.OutputPath(b.Path, settings));
        File.Delete(c.Path); File.Delete(EncodePlanner.OutputPath(c.Path, settings));

        var seen = new List<int>();
        engine.ProgressChanged += p => { lock (seen) seen.Add(p.Items.Count(i => i.OriginalDeleted || i.NewDeleted)); };
        Assert.Equal(3, await engine.VerifyFinishedFilesAsync());

        var items = engine.Current.Items;
        Assert.True(items[0].OriginalDeleted && !items[0].NewDeleted);
        Assert.True(!items[1].OriginalDeleted && items[1].NewDeleted);
        Assert.True(items[2].OriginalDeleted && items[2].NewDeleted);
        Assert.Contains(1, seen); Assert.Contains(2, seen); Assert.Contains(3, seen);   // each row was published as soon as it was known
        Assert.Equal(3, items.Count);                                                   // the rows stay, greyed out

        // It is kept: a restart shows the same, and a second look finds nothing new
        Assert.Equal(0, await engine.VerifyFinishedFilesAsync());
        var again = NewEngine(new FakeRunner());
        Assert.True(again.Current.Items[2].OriginalDeleted && again.Current.Items[2].NewDeleted);
    }

    [Fact]
    public async Task AFolderThatCannotBeReachedIsNotTakenForDeletedFiles()
    {
        var engine = NewEngine(new FakeRunner());
        var a = Video("a.mp4");
        engine.Enqueue(new[] { a }, EncodeSettings.CreateDefault());
        WaitFor(() => engine.Current.DoneCount == 1 && !engine.Current.Running, "done");

        Assert.False(EncodeEngine.IsMissing(a.Path));                                              // there
        Assert.True(EncodeEngine.IsMissing(Path.Combine(dir, "not-there.mp4")));                   // the folder is reachable, the file is not
        Assert.False(EncodeEngine.IsMissing(Path.Combine(dir, "no-such-folder", "x.mp4")));        // an unreachable folder (a share that is off) tells nothing

        // The whole folder is out of reach: nothing is marked
        Directory.Move(dir, dir + "-away");
        try { Assert.Equal(0, await engine.VerifyFinishedFilesAsync()); }
        finally { Directory.Move(dir + "-away", dir); }
        Assert.False(engine.Current.Items[0].OriginalDeleted || engine.Current.Items[0].NewDeleted);
    }

    [Fact]
    public void FilesDeletedByReviewOrTheListsAreNotedInTheFinishedVideos()
    {
        var engine = NewEngine(new FakeRunner());
        var a = Video("a.mp4"); var b = Video("b.mp4", 2);
        var settings = EncodeSettings.CreateDefault();
        engine.Enqueue(new[] { a, b }, settings);
        WaitFor(() => engine.Current.DoneCount == 2 && !engine.Current.Running, "done");
        var published = 0;
        engine.ProgressChanged += _ => published++;

        Assert.Equal(0, engine.NoteDeleted(new[] { Path.Combine(dir, "other.mp4"), "" }));         // not in the queue: nothing changes
        Assert.Equal(0, published);

        Assert.Equal(1, engine.NoteDeleted(new[] { a.Path }));   // an original
        Assert.Equal(1, engine.NoteDeleted(new[] { EncodePlanner.OutputPath(b.Path, settings) }));              // a new file
        var items = engine.Current.Items;
        Assert.True(items[0].OriginalDeleted && !items[0].NewDeleted);
        Assert.True(!items[1].OriginalDeleted && items[1].NewDeleted);
        Assert.Equal(2, published);
        Assert.Equal(0, engine.NoteDeleted(new[] { a.Path }));                                      // already known
        Assert.False(engine.Current.StatusByPath().ContainsKey(b.Path));                            // b's new file is gone: planned again like any video
    }

    [Fact]
    public void DismissLeavesWaitingVideosAlone()
    {
        var runner = new FakeRunner { Behavior = FakeRunner.Hang };
        var engine = NewEngine(runner);
        engine.Enqueue(new[] { Video("a.mp4"), Video("b.mp4", 2) }, EncodeSettings.CreateDefault());
        WaitFor(() => engine.Current.Current != null, "running");
        Assert.False(engine.Dismiss(engine.Current.Items.First(i => i.Status == EncodeItemStatus.Queued).Id));
        Assert.False(engine.Dismiss(engine.Current.Current!.Id));
        Assert.Equal(2, engine.Current.Items.Count);
    }

    // A queue file as the program wrote it before the check existed: no Check, no pictures
    private void WriteLegacyQueue(params (string path, string output, EncodeItemStatus status, string message, long bytes)[] items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        var list = items.Select((x, n) => new EncodeItem
        {
            VideoId = n + 1, Path = x.path, SizeBytes = 4000, DurationSeconds = 100, OutputPath = x.output,
            Status = x.status, Message = x.message, OutputBytes = x.bytes, FinishedUtc = DateTime.UtcNow.AddDays(-1),
        }).ToList();
        File.WriteAllText(state, System.Text.Json.JsonSerializer.Serialize(new { Items = list }));
    }

    [Fact]
    public async Task FinishedItemsFromBeforeTheCheckGetCheckedAndShowLikeTheOthers()
    {
        var settings = EncodeSettings.CreateDefault();
        var good = Video("good.mp4", 1); var skipped = Video("skipped.mp4", 2); var gone = Video("gone.mp4", 3);
        string goodOut = EncodePlanner.OutputPath(good.Path, settings), skippedOut = EncodePlanner.OutputPath(skipped.Path, settings), goneOut = EncodePlanner.OutputPath(gone.Path, settings);
        File.WriteAllText(goodOut, "encoded earlier"); File.WriteAllText(skippedOut, "made by an old script");
        WriteLegacyQueue(
            (good.Path, goodOut, EncodeItemStatus.Done, "", 15),
            (skipped.Path, skippedOut, EncodeItemStatus.Skipped, "the new file already exists", 0),
            (gone.Path, goneOut, EncodeItemStatus.Done, "", 99));

        var engine = NewEngine(new FakeRunner());
        Assert.All(engine.Current.Items, i => Assert.True(i.CheckPending || i.Status == EncodeItemStatus.Skipped));   // layout is side by side from the start
        var finishedBefore = engine.Current.Items.Select(i => i.FinishedUtc).ToList();
        await engine.CheckUncheckedAsync();

        var items = engine.Current.Items;
        Assert.Equal(EncodeItemStatus.Done, items[0].Status);
        Assert.True(items[0].CheckOk && items[0].HasCheckShots && items[0].CanDeleteOriginal);
        Assert.Equal(EncodeItemStatus.AlreadyEncoded, items[1].Status);          // "skipped: already exists" is now a proper result
        Assert.Equal("made by an old script".Length, items[1].OutputBytes);
        Assert.True(items[1].CheckOk && items[1].HasCheck);
        Assert.Equal(EncodeItemStatus.Done, items[2].Status);                    // its new file is gone: said so, not checked again
        Assert.Equal("The new file is not there any more", items[2].Check);
        Assert.False(items[2].CheckPending);
        for (int n = 1; n <= 3; n++) Assert.True(File.Exists(engine.CheckShotPath(items[0].Id, n)));
        Assert.Equal(finishedBefore, items.Select(i => i.FinishedUtc).ToList());   // the list keeps its order

        // Pictures that have gone missing are made again
        File.Delete(engine.CheckShotPath(items[0].Id, 1));
        await engine.CheckUncheckedAsync();
        Assert.True(File.Exists(engine.CheckShotPath(items[0].Id, 1)));
        Assert.Equal(EncodeItemStatus.Done, engine.Current.Items[0].Status);

        // and it is not done again
        var runner = new FakeRunner();
        var again = NewEngine(runner);
        await again.CheckUncheckedAsync();
        Assert.Empty(runner.PictureSeconds);
    }

    [Fact]
    public async Task AnOldDoneItemWhoseNewFileIsIncompleteBecomesIncomplete()
    {
        var settings = EncodeSettings.CreateDefault();
        var a = Video("a.mp4", 1);
        string output = EncodePlanner.OutputPath(a.Path, settings);
        File.WriteAllText(output, "cut off");
        WriteLegacyQueue((a.Path, output, EncodeItemStatus.Done, "", 7));
        var engine = NewEngine(new FakeRunner { ProbeSeconds = 5 });
        await engine.CheckUncheckedAsync();
        var item = engine.Current.Items.Single();
        Assert.Equal(EncodeItemStatus.Incomplete, item.Status);
        Assert.Contains("0:05 long", item.Message);
        Assert.True(item.CanReplace);
        Assert.False(item.CanDeleteOriginal);
    }

    [Fact]
    public void RecycleBinSaysWhenThereIsNone()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(RecycleBin.IsRecoverable(@"\\nas\video\movie.mp4"));   // a network share has no Recycle Bin
    }

    [Fact]
    public void VideoThatIsGoneIsSkipped()
    {
        var engine = NewEngine(new FakeRunner());
        var a = Video("a.mp4");
        File.Delete(a.Path);
        engine.Enqueue(new[] { a }, EncodeSettings.CreateDefault());
        WaitFor(() => engine.Current.SkippedCount == 1, "skipped");
        Assert.Contains("not there", engine.Current.Items[0].Message);
    }

    [Fact]
    public void StopPutsTheRunningVideoBackAndStartResumes()
    {
        var runner = new FakeRunner { Behavior = FakeRunner.Hang };
        var engine = NewEngine(runner);
        var a = Video("a.mp4"); var b = Video("b.mp4", 2);
        engine.Enqueue(new[] { a, b }, EncodeSettings.CreateDefault());
        WaitFor(() => engine.Current.Current?.Fraction > 0, "progress");
        var running = engine.Current.Current!;

        engine.Stop();
        WaitFor(() => !engine.Current.Running, "stopped");
        Assert.True(engine.Current.Halted);
        Assert.Equal(2, engine.Current.QueuedCount);
        Assert.Null(engine.Current.Current);
        Assert.False(File.Exists(Part(running)));

        // Adding while stopped does not start it
        engine.Enqueue(new[] { Video("c.mp4", 3) }, EncodeSettings.CreateDefault());
        Thread.Sleep(200);
        Assert.False(engine.Current.Running);
        Assert.Equal(3, engine.Current.QueuedCount);

        runner.Behavior = null;
        engine.Start();
        WaitFor(() => engine.Current.DoneCount == 3 && !engine.Current.Running, "all done after start");
        Assert.False(engine.Current.Halted);
    }

    [Fact]
    public void SkipCurrentMarksItSkippedAndContinues()
    {
        int call = 0;
        var runner = new FakeRunner
        {
            Behavior = (args, o, e, ct) => Interlocked.Increment(ref call) == 1 ? FakeRunner.Hang(args, o, e, ct) : FakeRunner.Succeed(args, o, e, ct)
        };
        var engine = NewEngine(runner);
        var a = Video("a.mp4"); var b = Video("b.mp4", 2);
        engine.Enqueue(new[] { a, b }, EncodeSettings.CreateDefault());
        WaitFor(() => engine.Current.Current?.Fraction > 0, "progress");
        var first = engine.Current.Current!;
        engine.SkipCurrent();
        WaitFor(() => engine.Current.DoneCount == 1 && !engine.Current.Running, "second done");
        Assert.Equal(EncodeItemStatus.Skipped, engine.Current.Items.First(i => i.Id == first.Id).Status);
        Assert.False(File.Exists(Part(first)));
    }

    [Fact]
    public void RemoveOnlyTakesWaitingVideos()
    {
        var runner = new FakeRunner { Behavior = FakeRunner.Hang };
        var engine = NewEngine(runner);
        engine.Enqueue(new[] { Video("a.mp4"), Video("b.mp4", 2) }, EncodeSettings.CreateDefault());
        WaitFor(() => engine.Current.Current != null, "running");
        Assert.False(engine.Remove(engine.Current.Current!.Id));
        var waiting = engine.Current.Items.First(i => i.Status == EncodeItemStatus.Queued);
        Assert.True(engine.Remove(waiting.Id));
        Assert.Single(engine.Current.Items);
    }

    [Fact]
    public void WaitingVideosCanBeReordered_AndAreEncodedInTheNewOrder()
    {
        var runner = new FakeRunner { Behavior = FakeRunner.Hang };
        var engine = NewEngine(runner);
        var videos = new[] { Video("a.mp4", 1), Video("b.mp4", 2), Video("c.mp4", 3), Video("d.mp4", 4) };
        engine.Enqueue(videos, EncodeSettings.CreateDefault());
        WaitFor(() => engine.Current.Current != null, "a running");
        string Id(string name) => engine.Current.Items.First(i => i.Path.EndsWith(name)).Id;
        string[] Waiting() => engine.Current.Items.Where(i => i.Status == EncodeItemStatus.Queued).Select(i => Path.GetFileName(i.Path)).ToArray();

        Assert.True(engine.Move(Id("d.mp4"), Id("b.mp4")));     // dragged above b
        Assert.Equal(new[] { "d.mp4", "b.mp4", "c.mp4" }, Waiting());
        Assert.True(engine.Move(Id("d.mp4"), null));              // dragged to the end
        Assert.Equal(new[] { "b.mp4", "c.mp4", "d.mp4" }, Waiting());
        Assert.True(engine.Move(Id("c.mp4"), Id("b.mp4")));
        Assert.Equal(new[] { "c.mp4", "b.mp4", "d.mp4" }, Waiting());
        Assert.False(engine.Move(Id("a.mp4"), Id("b.mp4")));     // the one being encoded stays where it is
        Assert.False(engine.Move(Id("b.mp4"), Id("a.mp4")));     // and nothing goes before it
        Assert.False(engine.Move(Id("b.mp4"), Id("b.mp4")));

        // The new order is kept (a restart reads it back, with the one that was being encoded first again) and is the order of encoding
        Assert.Equal(new[] { "a.mp4", "c.mp4", "b.mp4", "d.mp4" }, NewEngine(new FakeRunner()).Current.Items.Where(i => i.Status == EncodeItemStatus.Queued).Select(i => Path.GetFileName(i.Path)).ToArray());
        runner.Behavior = FakeRunner.Succeed;
        engine.SkipCurrent();
        WaitFor(() => engine.Current.DoneCount == 3 && !engine.Current.Running, "the rest done");
        Assert.Equal(new[] { "a.mp4", "c.mp4", "b.mp4", "d.mp4" }, runner.Inputs.Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void TheQueueSurvivesARestartAndWaitsForStart()
    {
        var runner = new FakeRunner { Behavior = FakeRunner.Hang };
        var first = NewEngine(runner);
        var a = Video("a.mp4"); var b = Video("b.mp4", 2);
        first.Enqueue(new[] { a, b }, EncodeSettings.CreateDefault());
        WaitFor(() => first.Current.Current?.Fraction > 0, "progress");
        var runningPart = Part(first.Current.Current!);
        first.Shutdown();   // the program closes

        // A crash instead leaves the video saved as "running", with its partial file behind
        File.WriteAllText(runningPart, "leftover of a crash");
        var saved = File.ReadAllText(state);
        int at = saved.IndexOf("\"Status\": 0", StringComparison.Ordinal);
        File.WriteAllText(state, saved.Remove(at, 11).Insert(at, "\"Status\": 1"));

        var second = NewEngine(new FakeRunner());
        Assert.Equal(2, second.Current.QueuedCount);
        Assert.True(second.Current.Halted);
        Assert.False(second.Current.Running);
        Assert.False(File.Exists(runningPart));          // stale partial file cleaned up
        Assert.Equal(EncodeItemStatus.Queued, second.Current.Items[0].Status);

        second.Start();
        WaitFor(() => second.Current.DoneCount == 2 && !second.Current.Running, "resumed and done");
    }

    [Fact]
    public void FinishedVideosAreRememberedAndCanBeCleared()
    {
        var engine = NewEngine(new FakeRunner());
        var a = Video("a.mp4");
        engine.Enqueue(new[] { a }, EncodeSettings.CreateDefault());
        WaitFor(() => engine.Current.DoneCount == 1 && !engine.Current.Running, "done");
        Assert.Equal(EncodeItemStatus.Done, engine.Current.StatusByPath()[a.Path]);

        var again = NewEngine(new FakeRunner());      // restart: still known
        Assert.Equal(1, again.Current.DoneCount);
        Assert.Equal(1, again.ClearFinished());
        Assert.Empty(again.Current.StatusByPath());
    }

    [Fact]
    public void FailedVideoCanBeQueuedAgain()
    {
        var runner = new FakeRunner { Behavior = (a, o, e, ct) => Task.FromResult(1) };
        var engine = NewEngine(runner);
        var a = Video("a.mp4");
        engine.Enqueue(new[] { a }, EncodeSettings.CreateDefault());
        WaitFor(() => engine.Current.FailedCount == 1 && !engine.Current.Running, "failed");
        runner.Behavior = null;
        Assert.Equal(1, engine.Enqueue(new[] { a }, EncodeSettings.CreateDefault()).Added);   // replaces the failed entry
        WaitFor(() => engine.Current.DoneCount == 1 && !engine.Current.Running, "done");
        Assert.Single(engine.Current.Items);
    }

    [Fact]
    public void FFmpegCannotBePreparedStopsTheQueueWithAMessage()
    {
        var engine = new EncodeEngine(state, () => throw new InvalidOperationException("no network"), new FakeRunner());
        engines.Add(engine);
        engine.Enqueue(new[] { Video("a.mp4") }, EncodeSettings.CreateDefault());
        WaitFor(() => engine.Current.Error != "" && !engine.Current.Running, "error");
        Assert.Contains("no network", engine.Current.Error);
        Assert.Equal(1, engine.Current.QueuedCount);
        Assert.True(engine.Current.Halted);
    }

    [Fact]
    public void ProgressTextsAreReadable()
    {
        var item = new EncodeItemInfo("1", 1, "/v/a.mp4", 1_000_000_000, 600, "x", "/v/a_x.mp4", EncodeItemStatus.Running, "", 0, 0.5, 30, 2.0,
            DateTime.UtcNow, DateTime.UtcNow, null);
        Assert.Equal("Encoding 50%", EncodeText.Status(item));
        Assert.Equal(TimeSpan.FromSeconds(150), item.Remaining);
        Assert.Contains($"{2.0:0.0}x speed", EncodeText.CurrentDetail(item));   // the number follows the PC's language
        Assert.Equal("Nothing in the queue", EncodeText.Headline(new EncodeProgress()));
    }
}

/// <summary>Encodes a real clip with the program's own FFmpeg (does nothing where there is none).</summary>
public class RealFFmpegTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dvr-real-" + Guid.NewGuid().ToString("N"));

    public RealFFmpegTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    private static string? FindFFmpeg()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "ffmpeg", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    [Fact]
    public async Task EncodesARealVideoWithProgress_AndStopLeavesNoPartFile()
    {
        string? ffmpeg = FindFFmpeg();
        if (ffmpeg == null) return;

        string source = Path.Combine(dir, "clip.mp4");
        var made = await new ProcessEncodeRunner().RunAsync(ffmpeg, new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc=duration=6:size=1280x720:rate=25",
            "-f", "lavfi", "-i", "sine=duration=6", "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", source }, _ => { }, _ => { }, CancellationToken.None);
        Assert.Equal(0, made);

        var settings = EncodeSettings.CreateDefault();
        settings.codec = "x264"; settings.crf = 28; settings.preset = "veryfast"; settings.resolution = 720; settings.container = "mkv";
        var video = new EncodeCandidate { Id = 1, Path = source, SizeBytes = new FileInfo(source).Length, DurationSeconds = 6 };

        var engine = new EncodeEngine(Path.Combine(dir, "state", "q.json"), () => Task.FromResult(ffmpeg));
        var seen = new List<double>();
        engine.ProgressChanged += p => { if (p.Current != null) lock (seen) seen.Add(p.Current.Fraction); };
        try
        {
            engine.Enqueue(new[] { video }, settings);
            var until = DateTime.UtcNow.AddSeconds(60);
            while ((engine.Current.DoneCount + engine.Current.FailedCount == 0 || engine.Current.Running) && DateTime.UtcNow < until) await Task.Delay(50);

            var item = engine.Current.Items.Single();
            Assert.True(item.Status == EncodeItemStatus.Done, item.Message + "\n" + string.Join("\n", engine.Current.Log));
            Assert.EndsWith("_720p_x264.mkv", item.OutputPath);
            Assert.True(File.Exists(item.OutputPath) && new FileInfo(item.OutputPath).Length > 0);
            Assert.False(File.Exists(EncodePlanner.PartPath(item.OutputPath)));
            lock (seen) Assert.Contains(seen, f => f > 0 && f < 1);   // progress arrived while it ran
        }
        finally { engine.Shutdown(); }
    }

    [Fact]
    public async Task StoppingKillsFFmpegAndDeletesThePartFile()
    {
        string? ffmpeg = FindFFmpeg();
        if (ffmpeg == null) return;

        // A long clip that cannot finish before we stop it
        string source = Path.Combine(dir, "long.mp4");
        Assert.Equal(0, await new ProcessEncodeRunner().RunAsync(ffmpeg, new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=duration=600:size=1920x1080:rate=30",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", source }, _ => { }, _ => { }, CancellationToken.None));

        var settings = EncodeSettings.CreateDefault();
        settings.codec = "x265"; settings.preset = "slower"; settings.resolution = 1080;
        var video = new EncodeCandidate { Id = 1, Path = source, SizeBytes = new FileInfo(source).Length, DurationSeconds = 600 };
        var engine = new EncodeEngine(Path.Combine(dir, "state", "q.json"), () => Task.FromResult(ffmpeg));
        engine.Enqueue(new[] { video }, settings);
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!(engine.Current.Current?.Fraction > 0) && DateTime.UtcNow < until) await Task.Delay(50);
        string part = EncodePlanner.PartPath(engine.Current.Items[0].OutputPath);
        Assert.True(File.Exists(part));

        engine.Shutdown();
        Assert.False(File.Exists(part));
        Assert.Equal(EncodeItemStatus.Queued, engine.Current.Items[0].Status);
        // (Deleting the .part file above only works once FFmpeg has really let go of it, so it was killed.)
    }
}

/// <summary>The check against real files made by the program's own FFmpeg (does nothing where there is none).</summary>
public class RealCheckTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dvr-check-" + Guid.NewGuid().ToString("N"));

    public RealCheckTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    private static string? FindFFmpeg()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "ffmpeg", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    [Fact]
    public async Task GoodFileChecksOut_CutOffFileIsIncomplete_EvenThoughItsHeaderSaysFullLength()
    {
        string? ffmpeg = FindFFmpeg();
        if (ffmpeg == null) return;

        string source = Path.Combine(dir, "clip.mp4");
        Assert.Equal(0, await new ProcessEncodeRunner().RunAsync(ffmpeg, new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=duration=10:size=1280x720:rate=25",
            "-f", "lavfi", "-i", "sine=duration=10", "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", source }, _ => { }, _ => { }, CancellationToken.None));

        var settings = EncodeSettings.CreateDefault();
        settings.codec = "x264"; settings.crf = 28; settings.preset = "veryfast"; settings.resolution = 720; settings.container = "mp4";
        var video = new EncodeCandidate { Id = 1, Path = source, SizeBytes = new FileInfo(source).Length, DurationSeconds = 10 };
        string output = EncodePlanner.OutputPath(source, settings);

        var engine = new EncodeEngine(Path.Combine(dir, "state", "q.json"), () => Task.FromResult(ffmpeg));
        async Task WaitIdle()
        {
            var until = DateTime.UtcNow.AddSeconds(90);
            await Task.Delay(300);
            while ((engine.Current.Running || engine.Current.QueuedCount > 0) && DateTime.UtcNow < until) await Task.Delay(100);
        }
        try
        {
            // 1. Encoded now: checked straight away, three real pictures that decode
            engine.Enqueue(new[] { video }, settings);
            await WaitIdle();
            var done = engine.Current.Items.Single();
            Assert.True(done.Status == EncodeItemStatus.Done, done.Message + "\n" + string.Join("\n", engine.Current.Log.TakeLast(8)));
            Assert.True(done.CheckOk && done.CanDeleteOriginal, done.Check);
            for (int n = 1; n <= 3; n++)
            {
                using var picture = SkiaSharp.SKBitmap.Decode(engine.CheckShotPath(done.Id, n));
                Assert.NotNull(picture);
                Assert.Equal(240, picture.Width);
                Assert.Equal(160, picture.Height);
            }

            // 2. A finished result from an earlier run: recognised, nothing encoded
            byte[] good = File.ReadAllBytes(output);
            engine.ClearFinished();
            engine.Enqueue(new[] { video }, settings);
            await WaitIdle();
            Assert.Equal(EncodeItemStatus.AlreadyEncoded, engine.Current.Items.Single().Status);
            Assert.True(engine.Current.Items.Single().CheckOk);
            Assert.Equal(good.Length, new FileInfo(output).Length);

            // 3. The same file cut off at 60%: mp4 keeps the header (and so the length) at the start, only reading it shows the damage
            engine.ClearFinished();
            File.WriteAllBytes(output, good[..(good.Length * 6 / 10)]);
            engine.Enqueue(new[] { video }, settings);
            await WaitIdle();
            var cut = engine.Current.Items.Single();
            Assert.True(cut.Status == EncodeItemStatus.Incomplete, $"{cut.Status}: {cut.Message}");
            Assert.False(cut.CheckOk);
            Assert.False(cut.CanDeleteOriginal);
            Assert.True(cut.CanReplace);
            Assert.Equal(good.Length * 6 / 10, new FileInfo(output).Length);   // the engine itself never touches it
            Console.WriteLine("cut-off file: " + cut.Message);
        }
        finally { engine.Shutdown(); }
    }
}

public class SubtitleTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dvr-subs-" + Guid.NewGuid().ToString("N"));

    public SubtitleTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void Mkv_CopiesPictureAndTextSubtitles_ConvertsMovTextToSrt()
    {
        var args = Subtitles.Arguments("/v/a.mkv", "mkv", new[] { "subrip", "hdmv_pgs_subtitle", "mov_text" });
        Assert.Equal(new[] { "-map", "0:s:0", "-map", "0:s:1", "-map", "0:s:2", "-c:s:0", "copy", "-c:s:1", "copy", "-c:s:2", "srt" }, args);
    }

    [Fact]
    public void Mp4_ConvertsTextSubtitles_LeavesOutPictureSubtitles()
    {
        var args = Subtitles.Arguments("/v/a.mkv", "mp4", new[] { "hdmv_pgs_subtitle", "subrip", "dvd_subtitle", "ass" });
        Assert.Equal(new[] { "-map", "0:s:1", "-map", "0:s:3", "-c:s:0", "mov_text", "-c:s:1", "mov_text" }, args);
    }

    [Fact]
    public void NoSubtitles_NoArguments() => Assert.Empty(Subtitles.Arguments("/v/a.mkv", "mp4", Array.Empty<string>()));

    [Theory]
    [InlineData("/v/a.mkv", "mkv", "-map 0:s? -c:s copy")]
    [InlineData("/v/a.mp4", "mkv", "-map 0:s? -c:s srt")]
    [InlineData("/v/a.mp4", "mp4", "-map 0:s? -c:s mov_text")]
    [InlineData("/v/a.mkv", "mp4", "")]   // could be picture subtitles, which MP4 cannot hold
    public void Unknown_DoesOnlyWhatIsSafeForTheFileType(string input, string container, string expected) =>
        Assert.Equal(expected, string.Join(" ", Subtitles.Arguments(input, container, null)));

    [Fact]
    public void ScriptArgumentsIncludeSubtitles()
    {
        var e = EncodeSettings.CreateDefault();
        e.container = "mkv";
        var args = EncodePlanner.Arguments("/v/a.avi", e);
        Assert.Contains("0:s?", args);
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

    [Theory]
    [InlineData("mkv", "mp4", "mov_text")]
    [InlineData("mp4", "mkv", "subrip")]
    public async Task RealEncodeKeepsTheSubtitles(string sourceContainer, string target, string expectedCodec)
    {
        string? ffmpeg = FindFFmpeg();
        if (ffmpeg == null) return;
        var runner = new ProcessEncodeRunner();

        string srt = Path.Combine(dir, "subs.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:02,500\nHello\n\n2\n00:00:03,000 --> 00:00:05,000\nWorld\n");
        string source = Path.Combine(dir, "clip." + sourceContainer);
        Assert.Equal(0, await runner.RunAsync(ffmpeg, new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc=duration=6:size=640x360:rate=25", "-i", srt,
            "-map", "0", "-map", "1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:s", sourceContainer == "mp4" ? "mov_text" : "srt",
            "-metadata:s:s:0", "language=dan", source }, _ => { }, _ => { }, CancellationToken.None));

        var codecs = await Subtitles.ProbeAsync(runner, ffmpeg, source, CancellationToken.None);
        Assert.NotNull(codecs);
        Assert.Single(codecs!);

        var e = EncodeSettings.CreateDefault();
        e.codec = "x264"; e.crf = 30; e.preset = "veryfast"; e.resolution = 360; e.container = target;
        string output = Path.Combine(dir, "out." + target);
        var args = EncodePlanner.Arguments(source, e, output, codecs);
        var errors = new List<string>();
        Assert.True(0 == await runner.RunAsync(ffmpeg, args, _ => { }, l => errors.Add(l), CancellationToken.None), string.Join("\n", errors.TakeLast(5)));

        var lines = new List<string>();
        await runner.RunAsync(Subtitles.FfprobePath(ffmpeg), new[] { "-v", "error", "-select_streams", "s", "-show_entries", "stream=codec_name:stream_tags=language",
            "-of", "default=noprint_wrappers=1", output }, l => lines.Add(l), _ => { }, CancellationToken.None);
        Assert.Contains("codec_name=" + expectedCodec, lines);
        Assert.Contains("TAG:language=dan", lines);
    }
}

/// <summary>The scan's pictures of a video and of its re-encode must be the same frames, or the two never match as duplicates.</summary>
public class SameFrameTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dvr-frames-" + Guid.NewGuid().ToString("N"));
    public SameFrameTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    private static string? FindFFmpeg()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "ffmpeg", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    [Fact]
    public void PictureArguments_PickTheFirstFrameFromTheSecond()
    {
        var args = Picture.Arguments("/v/a b.mp4", 120, "/s/1.png", 240, 160);
        Assert.Equal("119", args[args.IndexOf("-ss") + 1]);                     // decoding starts a second before
        Assert.Equal(@"select=gte(t\,0.998),scale=240:160", args[args.IndexOf("-vf") + 1]);
        Assert.DoesNotContain("-ss", Picture.Arguments("/v/a.mp4", 0, "/s/1.png", 240, 160));
        Assert.Equal(new[] { "-fps_mode", "passthrough", "-update", "1", "/s/1.png" }, args.TakeLast(5));   // one picture, no repeated frames
    }

    private static double Difference(string a, string b)
    {
        using var x = SkiaSharp.SKBitmap.Decode(a);
        using var y = SkiaSharp.SKBitmap.Decode(b);
        double sum = 0;
        for (int i = 0; i < x.Width; i++)
            for (int j = 0; j < x.Height; j++)
            {
                var p = x.GetPixel(i, j); var q = y.GetPixel(i, j);
                sum += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
            }
        return sum / (x.Width * x.Height * 3);
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    public async Task AReEncodeGivesTheSamePictures_AlsoForAVariableFrameRateVideoThatStartsAfterItsSound(string container)
    {
        string? ffmpeg = FindFFmpeg();
        if (ffmpeg == null) return;
        var runner = new ProcessEncodeRunner();
        var errors = new List<string>();

        // Frames 0.05, 0.05, 0.02 s apart (variable), the picture starting 0.08 s after the sound; a moving test pattern, so
        // neighbouring frames look different
        string source = Path.Combine(dir, "vfr.mp4");
        Assert.True(0 == await runner.RunAsync(ffmpeg, new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=duration=12:size=640x360:rate=25",
            "-f", "lavfi", "-i", "sine=duration=12", "-vf", @"setpts='(N/25+0.01*mod(N\,3))/TB+0.08/TB'", "-fps_mode", "passthrough",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", source }, _ => { }, l => errors.Add(l), CancellationToken.None),
            string.Join("\n", errors.TakeLast(5)));

        var e = EncodeSettings.CreateDefault();
        e.codec = "x265"; e.crf = 20; e.preset = "veryfast"; e.resolution = 360; e.container = container;
        string output = Path.Combine(dir, "out." + container);
        errors.Clear();
        Assert.True(0 == await runner.RunAsync(ffmpeg, EncodePlanner.Arguments(source, e, output), _ => { }, l => errors.Add(l), CancellationToken.None),
            string.Join("\n", errors.TakeLast(5)));

        foreach (int second in new[] { 1, 3, 5, 8, 11 })
        {
            string a = Path.Combine(dir, $"a{second}.png"), b = Path.Combine(dir, $"b{second}.png");
            Assert.Equal(0, await runner.RunAsync(ffmpeg, Picture.Arguments(source, second, a, 240, 160), _ => { }, _ => { }, CancellationToken.None));
            Assert.Equal(0, await runner.RunAsync(ffmpeg, Picture.Arguments(output, second, b, 240, 160), _ => { }, _ => { }, CancellationToken.None));
            double d = Difference(a, b);
            Assert.True(d < 4, $"the pictures at {second} s differ by {d:0.0} (another frame)");
        }
    }
}

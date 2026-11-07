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

        public Task<int> RunAsync(string program, IReadOnlyList<string> args, Action<string> onOutput, Action<string> onError, CancellationToken ct)
        {
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
        }
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
        Assert.Contains("code 1", item.Message);
        Assert.Contains("Error opening input file", item.Message);
        Assert.False(File.Exists(Part(item)));
        Assert.False(File.Exists(item.OutputPath));
    }

    [Fact]
    public void ExistingResultIsSkippedWhenNeverOverwrite()
    {
        var runner = new FakeRunner();
        var engine = NewEngine(runner);
        var a = Video("a.mp4");
        var settings = EncodeSettings.CreateDefault();
        File.WriteAllText(EncodePlanner.OutputPath(a.Path, settings), "earlier result");
        engine.Enqueue(new[] { a }, settings);
        WaitFor(() => engine.Current.SkippedCount == 1, "skipped");
        Assert.Empty(runner.Inputs);
        Assert.Equal("earlier result", File.ReadAllText(EncodePlanner.OutputPath(a.Path, settings)));
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
        Assert.False(System.Diagnostics.Process.GetProcessesByName("ffmpeg").Any(p => { try { return p.MainModule?.FileName == ffmpeg && p.StartTime > DateTime.Now.AddMinutes(-2); } catch { return false; } }));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using deepduplicates;
using Xunit;

namespace CoreTests;

public class LivePictureTests
{
    private TimeSpan t = TimeSpan.Zero;
    private void Wait(double seconds) => t += TimeSpan.FromSeconds(seconds);

    // ---- The timer: the first picture at once, then one every 2.5 seconds

    [Fact]
    public void TheFirstPictureIsDueAtOnce_ThenEvery2AndAHalfSeconds()
    {
        var timer = new LivePictureTimer(() => t);
        Assert.True(timer.IsDue()); timer.Shown();
        Wait(2.4); Assert.False(timer.IsDue());
        Wait(0.1); Assert.True(timer.IsDue()); timer.Shown();            // 2.5 s after the last one
        Wait(1); Assert.False(timer.IsDue());
        Wait(1.5); Assert.True(timer.IsDue());
    }

    // ---- The pictures of a run: what they are taken from in each phase

    private sealed class Setup
    {
        public ScanPhase Phase = ScanPhase.FindingFiles;
        public List<string> Found = new();
        public Func<IReadOnlyList<string>, VideoInfo> Lookup = _ => null;
        public HashSet<int> WithoutPicture = new();
        public readonly List<VideoEntry> Shown = new();
        public LiveScanPictures Pictures = null!;
        public readonly List<string> LookedUp = new();
    }

    private Setup Start()
    {
        var s = new Setup();
        s.Pictures = new LiveScanPictures(() => s.Phase, () => s.Found,
            paths => { s.LookedUp.Add(string.Join(",", paths)); return s.Lookup(paths); },
            v => !s.WithoutPicture.Contains(v.id), s.Shown.Add, () => t, start: false);
        return s;
    }

    private static VideoInfo Video(int id) => new() { id = id, path = $"c:/v/{id}.mp4", duration = 60, fileSize = 1000 };

    [Fact]
    public void WhileTheFilesAreFound_APictureComesFromAFoundFileThatIsInTheDatabase_AtOnce_AndEvery2AndAHalfSeconds()
    {
        var s = Start();
        s.Found = new() { "c:/v/new1.mp4", "c:/v/2.mp4" };               // newest first
        s.Lookup = paths => Video(2);                                     // 2.mp4 was read before
        s.Pictures.Tick();
        Assert.Equal(new[] { 2 }, s.Shown.Select(e => e.Id));              // at once

        Wait(1); s.Lookup = paths => Video(3); s.Pictures.Tick();
        Assert.Single(s.Shown);                                           // too soon

        Wait(1.5); s.Pictures.Tick();
        Assert.Equal(new[] { 2, 3 }, s.Shown.Select(e => e.Id));           // 2.5 s after the first
        Wait(2.5); s.Pictures.Tick();
        Assert.Equal(2, s.Shown.Count);                                   // the same video again: nothing new to show
        Wait(0.1); s.Lookup = paths => Video(4); s.Pictures.Tick();
        Assert.Equal(new[] { 2, 3, 4 }, s.Shown.Select(e => e.Id));        // as soon as there is another
    }

    [Fact]
    public void WhileTheFilesAreFound_NothingIsShownUntilAFoundFileIsInTheDatabase()
    {
        var s = Start();
        s.Pictures.Tick();                                                // nothing found yet
        Assert.Empty(s.Shown); Assert.Empty(s.LookedUp);
        s.Found = new() { "c:/v/new.mp4" };                               // found, but not in the database: it is a new file
        s.Pictures.Tick();
        Assert.Empty(s.Shown);
        Wait(0.25); s.Lookup = _ => Video(7); s.Pictures.Tick();           // a tick later one is: shown at once (no 2.5 s wait: nothing was shown yet)
        Assert.Equal(new[] { 7 }, s.Shown.Select(e => e.Id));
    }

    [Fact]
    public void WhileTheDatabaseIsProcessed_ThePictureIsTheVideoTheScanIsAt_AndANewlyReadVideoIsShownAtOnce()
    {
        var s = Start();
        s.Phase = ScanPhase.ReadingVideos;
        s.Pictures.Seen(Video(1)); s.Pictures.Tick();
        Assert.Equal(new[] { 1 }, s.Shown.Select(e => e.Id));

        Wait(0.5); s.Pictures.Read(Video(2));                              // a new video was read: at once, not on the 2.5 s beat
        Assert.Equal(new[] { 1, 2 }, s.Shown.Select(e => e.Id));

        Wait(1); s.Pictures.Seen(Video(3)); s.Pictures.Tick();
        Assert.Equal(2, s.Shown.Count);                                   // 1 s after the last picture: not due
        Wait(1.5); s.Pictures.Tick();
        Assert.Equal(new[] { 1, 2, 3 }, s.Shown.Select(e => e.Id));        // due: the video the scan is at now
    }

    [Fact]
    public void WhileTheDatabaseLoads_ThereIsNoVideoYet_SoTheFoundFilesAreUsed()
    {
        var s = Start();
        s.Phase = ScanPhase.ReadingVideos;
        s.Found = new() { "c:/v/5.mp4" };
        s.Lookup = _ => Video(5);
        s.Pictures.Tick();
        Assert.Equal(new[] { 5 }, s.Shown.Select(e => e.Id));
    }

    [Fact]
    public void WhileComparing_ThePictureIsTheVideoBeingCompared()
    {
        var s = Start();
        s.Phase = ScanPhase.Comparing;
        s.Pictures.Seen(Video(1));
        s.Pictures.Tick();
        Assert.Equal(new[] { 1 }, s.Shown.Select(e => e.Id));              // nothing compared yet: the last one the scan was at
        Wait(2.5); s.Pictures.Compared(Video(9)); s.Pictures.Tick();
        Assert.Equal(new[] { 1, 9 }, s.Shown.Select(e => e.Id));
    }

    [Fact]
    public void AVideoWithoutPictures_IsPassedOver_AndTheNextTickTriesAnother_WithoutWaiting()
    {
        var s = Start();
        s.Phase = ScanPhase.ReadingVideos;
        s.WithoutPicture.Add(1);
        s.Pictures.Seen(Video(1)); s.Pictures.Tick();
        Assert.Empty(s.Shown);                                            // no pictures (it failed): nothing shown, and no time used up
        Wait(0.25); s.Pictures.Seen(Video(2)); s.Pictures.Tick();
        Assert.Equal(new[] { 2 }, s.Shown.Select(e => e.Id));
    }

    [Fact]
    public void NothingIsShownBeforeOrAfterTheRun()
    {
        var s = Start();
        s.Phase = ScanPhase.Preparing; s.Pictures.Seen(Video(1)); s.Pictures.Tick();
        s.Phase = ScanPhase.Done; Wait(5); s.Pictures.Tick();
        Assert.Empty(s.Shown);
    }

    [Fact]
    public void OnlyAVideoReadForTheFirstTimeIsMarkedNew()
    {
        var s = Start();
        s.Phase = ScanPhase.ReadingVideos;
        s.Pictures.Seen(Video(1));                                        // already in the database
        s.Pictures.Read(Video(2));                                        // read now
        Assert.Equal(new[] { false, true }, s.Shown.Select(e => e.IsNew));
    }
}

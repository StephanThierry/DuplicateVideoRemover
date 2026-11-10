using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace deepduplicates
{
    /// <summary>When the Scan page is due a new live picture: at once the first time, then every <see cref="Every"/>.</summary>
    public sealed class LivePictureTimer
    {
        public static readonly TimeSpan Every = TimeSpan.FromSeconds(2.5);

        private readonly Func<TimeSpan> now;
        private TimeSpan? lastShown;

        /// <param name="now">The time since the scan started (a stopwatch's elapsed time).</param>
        public LivePictureTimer(Func<TimeSpan> now) => this.now = now;

        public bool IsDue() => lastShown == null || now() - lastShown.Value >= Every;

        public void Shown() => lastShown = now();
    }

    /// <summary>
    /// The live pictures on the Scan page, for the whole run: a picture of a video at least every 2.5 seconds, taken from what the scan is doing -
    /// the files it has found (while it finds them, and while the database loads), the video it is at in the database, the video it compares.
    /// A video that was just read is shown at once. A video without pictures (not read yet, or it failed) is passed over.
    /// </summary>
    public sealed class LiveScanPictures : IDisposable
    {
        private readonly Func<ScanPhase> phase;
        private readonly Func<IReadOnlyList<string>> foundPaths;
        private readonly Func<IReadOnlyList<string>, VideoInfo> lookup;
        private readonly Func<VideoInfo, bool> hasPicture;
        private readonly Action<VideoEntry> show;
        private readonly LivePictureTimer timer;
        private readonly CancellationTokenSource stop = new();
        private readonly object sync = new();
        private int lastShownId;
        private bool lastWasNew;
        private volatile VideoInfo seen, compared;

        /// <param name="phase">What the scan is doing now.</param>
        /// <param name="foundPaths">The files found most recently, the newest first.</param>
        /// <param name="lookup">The first of these paths that is in the database, as a video (null: none is).</param>
        /// <param name="hasPicture">The video's screenshots are there.</param>
        /// <param name="show">Shows a picture.</param>
        /// <param name="clock">The time since the start (a stopwatch by default).</param>
        /// <param name="start">Look every quarter of a second by itself (tests call <see cref="Tick"/>).</param>
        public LiveScanPictures(Func<ScanPhase> phase, Func<IReadOnlyList<string>> foundPaths, Func<IReadOnlyList<string>, VideoInfo> lookup,
            Func<VideoInfo, bool> hasPicture, Action<VideoEntry> show, Func<TimeSpan> clock = null, bool start = true)
        {
            this.phase = phase; this.foundPaths = foundPaths; this.lookup = lookup; this.hasPicture = hasPicture; this.show = show;
            if (clock == null)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                clock = () => watch.Elapsed;
            }
            timer = new LivePictureTimer(clock);
            if (start) Task.Run(Loop);
        }

        /// <summary>The video the scan is at in the database now (read before, or just read).</summary>
        public void Seen(VideoInfo video) { seen = video; Tick(); }

        /// <summary>The video the comparing is at now.</summary>
        public void Compared(VideoInfo video) { compared = video; Tick(); }

        /// <summary>A video was read just now: its picture is shown at once.</summary>
        public void Read(VideoInfo video) => Show(video, true);

        /// <summary>Shows a picture when one is due: once every 2.5 seconds, the first at once.</summary>
        public void Tick()
        {
            if (!timer.IsDue()) return;
            VideoInfo candidate;
            switch (phase())
            {
                case ScanPhase.FindingFiles: candidate = FromFound(); break;
                case ScanPhase.ReadingVideos: candidate = seen ?? FromFound(); break;      // loading the database: the files found are known already
                case ScanPhase.Comparing: candidate = compared ?? seen; break;
                default: return;
            }
            if (candidate != null) Show(candidate);
        }

        private VideoInfo FromFound()
        {
            var paths = foundPaths();
            return paths.Count == 0 ? null : lookup(paths);
        }

        private void Show(VideoInfo video, bool isNew = false)
        {
            if (video == null || video.id == 0 || !hasPicture(video)) return;
            lock (sync)
            {
                if (video.id == lastShownId && !(isNew && !lastWasNew)) return;   // the same one again: nothing new to show (unless it turns out to be a new file)
                lastShownId = video.id; lastWasNew = isNew;
                timer.Shown();
            }
            var entry = VideoEntry.From(video);
            entry.IsNew = isNew;
            show(entry);
        }

        private async Task Loop()
        {
            try
            {
                using var ticks = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                while (await ticks.WaitForNextTickAsync(stop.Token))
                {
                    try { Tick(); }
                    catch (Exception) { /* a picture is a nicety: the scan goes on */ }
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose() => stop.Cancel();

        /// <summary>
        /// The first of the paths (newest first) that has a row in the database, as a video with what the pictures need. Looks the paths up in
        /// one query. Null when none is there, or the database can't be read (a new database has no table yet).
        /// </summary>
        public static VideoInfo LookupInDatabase(IReadOnlyList<string> paths)
        {
            try
            {
                var wanted = paths.Take(60).ToList();
                using var db = new VideoInfoContext();
                var rows = db.VideoInfos.AsNoTracking().Where(v => wanted.Contains(v.path))
                    .Select(v => new { v.id, v.path, v.duration, v.fileSize, v.bitrate }).ToList();
                foreach (var path in wanted)
                {
                    var row = rows.FirstOrDefault(r => string.Equals(r.path, path, StringComparison.OrdinalIgnoreCase));
                    if (row != null) return new VideoInfo { id = row.id, path = row.path, duration = row.duration, fileSize = row.fileSize, bitrate = row.bitrate };
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}

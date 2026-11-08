#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace deepduplicates
{
    /// <summary>
    /// Done: encoded now. AlreadyEncoded: the new file was there already and checked out. Incomplete: it was there
    /// and did not check out. (Saved in encodequeue.json as numbers: only ever add at the end.)
    /// </summary>
    public enum EncodeItemStatus { Queued, Running, Done, Failed, Skipped, AlreadyEncoded, Incomplete }

    /// <summary>One video in the encode queue, as the interfaces see it (a snapshot).</summary>
    public sealed record EncodeItemInfo(
        string Id, int VideoId, string Path, long SizeBytes, int DurationSeconds,
        string Summary, string OutputPath, EncodeItemStatus Status, string Message,
        long OutputBytes, double Fraction, double Fps, double Speed,
        DateTime QueuedUtc, DateTime? StartedUtc, DateTime? FinishedUtc,
        string Check = "", bool CheckOk = false, bool HasCheckShots = false, bool OriginalDeleted = false, bool NewDeleted = false)
    {
        public string FileName => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
        public bool IsFinished => Status is EncodeItemStatus.Done or EncodeItemStatus.Failed or EncodeItemStatus.Skipped
            or EncodeItemStatus.AlreadyEncoded or EncodeItemStatus.Incomplete;
        /// <summary>Bytes saved by a finished encode (can be negative when the new file is bigger).</summary>
        public long Saved => Status is EncodeItemStatus.Done or EncodeItemStatus.AlreadyEncoded ? SizeBytes - OutputBytes : 0;

        /// <summary>The new file was checked (length and pictures) and is good: the original can be deleted after a look.</summary>
        public bool CanDeleteOriginal => Status is EncodeItemStatus.Done or EncodeItemStatus.AlreadyEncoded or EncodeItemStatus.Incomplete
            && !OriginalDeleted && (CheckOk || NewDeleted);
        /// <summary>
        /// The new file can be deleted (to the Recycle Bin), whatever happened to the original: each copy is handled on its own,
        /// the interface says when it is the last one. The file's existence is checked when it is deleted.
        /// </summary>
        public bool CanDeleteNew => Status is EncodeItemStatus.Done or EncodeItemStatus.AlreadyEncoded or EncodeItemStatus.Incomplete && !NewDeleted;
        /// <summary>The new file that was there already is not complete: it can be replaced by encoding again.</summary>
        public bool CanReplace => Status == EncodeItemStatus.Incomplete;
        /// <summary>
        /// A new file exists for this video, so it is shown with the original next to it (the check's pictures and result).
        /// Items from before the check existed get theirs in the background: until then <see cref="CheckPending"/> is true.
        /// </summary>
        public bool HasCheck => Status is EncodeItemStatus.Done or EncodeItemStatus.AlreadyEncoded or EncodeItemStatus.Incomplete;
        /// <summary>The new file has not been checked yet.</summary>
        public bool CheckPending => HasCheck && !CheckOk && Check.Length == 0 && Message.Length == 0;

        /// <summary>Time left for the running video, from the encoder's speed (null when not known yet).</summary>
        public TimeSpan? Remaining =>
            Status == EncodeItemStatus.Running && Speed > 0.01 && DurationSeconds > 0
                ? TimeSpan.FromSeconds((1 - Fraction) * DurationSeconds / Speed)
                : null;
    }

    /// <summary>A snapshot of the encode queue and the run in progress, reported to the interfaces.</summary>
    public sealed record EncodeProgress
    {
        public IReadOnlyList<EncodeItemInfo> Items { get; init; } = Array.Empty<EncodeItemInfo>();

        /// <summary>True while the encoder is working through the queue.</summary>
        public bool Running { get; init; }

        /// <summary>True when the queue was stopped (by the user, or by a restart): it waits for "Start".</summary>
        public bool Halted { get; init; }

        /// <summary>Why the encoder stopped by itself (FFmpeg could not be prepared, another program is encoding...).</summary>
        public string Error { get; init; } = "";

        /// <summary>The most recent lines: what started and finished, and what FFmpeg said.</summary>
        public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();

        /// <summary>Time since this run started (the run lasts until the queue is empty).</summary>
        public TimeSpan Elapsed { get; init; }

        /// <summary>Estimated time left of the whole queue, when it can be estimated.</summary>
        public TimeSpan? Remaining { get; init; }

        /// <summary>0..1: video minutes finished of the video minutes in this run (so long videos weigh more).</summary>
        public double Fraction { get; init; }

        public EncodeItemInfo? Current => Items.FirstOrDefault(i => i.Status == EncodeItemStatus.Running);
        public int QueuedCount => Items.Count(i => i.Status == EncodeItemStatus.Queued);
        public int DoneCount => Items.Count(i => i.Status == EncodeItemStatus.Done);
        public int FailedCount => Items.Count(i => i.Status == EncodeItemStatus.Failed);
        public int SkippedCount => Items.Count(i => i.Status == EncodeItemStatus.Skipped);
        public int AlreadyEncodedCount => Items.Count(i => i.Status == EncodeItemStatus.AlreadyEncoded);
        public int IncompleteCount => Items.Count(i => i.Status == EncodeItemStatus.Incomplete);
        public int PendingCount => QueuedCount + (Current != null ? 1 : 0);
        public int FinishedCount => Items.Count(i => i.IsFinished);

        /// <summary>Size of the finished videos before and after, and what that saved.</summary>
        public long SavedBytes => Items.Where(i => i.Status == EncodeItemStatus.Done && !i.NewDeleted).Sum(i => i.Saved);
        public long DoneOriginalBytes => Items.Where(i => i.Status == EncodeItemStatus.Done && !i.NewDeleted).Sum(i => i.SizeBytes);
        public long DoneNewBytes => Items.Where(i => i.Status == EncodeItemStatus.Done && !i.NewDeleted).Sum(i => i.OutputBytes);

        /// <summary>Videos that are in the queue or have a good new file, by path (to grey them out in the Re-encode list).</summary>
        public Dictionary<string, EncodeItemStatus> StatusByPath()
        {
            var map = new Dictionary<string, EncodeItemStatus>(PathComparer);
            foreach (var i in Items)
                // A video whose new file was deleted is a video to plan again
                if (i.Status is EncodeItemStatus.Queued or EncodeItemStatus.Running || i.Status is EncodeItemStatus.Done or EncodeItemStatus.AlreadyEncoded && !i.NewDeleted) map[i.Path] = i.Status;
            return map;
        }

        public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }

    /// <summary>
    /// Reads the lines FFmpeg writes with <c>-progress pipe:1</c>: blocks of key=value lines, each ending with
    /// progress=continue (or progress=end after the last one). Early blocks can hold "N/A" values.
    /// </summary>
    public sealed class FfmpegProgressParser
    {
        /// <summary>How far the output has come (null until FFmpeg knows).</summary>
        public TimeSpan? OutTime { get; private set; }
        public double Fps { get; private set; }
        /// <summary>Encoding speed as a multiple of real time (2.0 = twice as fast as playing).</summary>
        public double Speed { get; private set; }
        public long TotalSize { get; private set; }
        public bool Ended { get; private set; }

        /// <summary>Feeds one line. Returns true when it completed a block (the values above are up to date).</summary>
        public bool Feed(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            int eq = line.IndexOf('=');
            if (eq <= 0) return false;
            string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
            switch (key)
            {
                // out_time_ms is in microseconds too (a long-standing FFmpeg naming mistake)
                case "out_time_us":
                case "out_time_ms":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us)) OutTime = TimeSpan.FromTicks(Math.Max(us, 0) * 10);
                    break;
                case "out_time":
                    if (OutTime == null && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var t)) OutTime = t < TimeSpan.Zero ? TimeSpan.Zero : t;
                    break;
                case "fps":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps)) Fps = fps;
                    break;
                case "speed":
                    if (double.TryParse(value.TrimEnd('x', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out double sp)) Speed = sp;
                    break;
                case "total_size":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)) TotalSize = size;
                    break;
                case "progress":
                    Ended = value == "end";
                    return true;
            }
            return false;
        }

        /// <summary>0..1 of a video of this length (never 1 before FFmpeg says it has ended).</summary>
        public double FractionOf(int durationSeconds) =>
            OutTime is TimeSpan t && durationSeconds > 0 ? Math.Clamp(t.TotalSeconds / durationSeconds, 0, Ended ? 1 : 0.999) : 0;
    }

    /// <summary>The texts shown with the encode queue, shared by the web and desktop apps.</summary>
    public static class EncodeText
    {
        public static string Summary(EncodeSettings e) =>
            $"{EncodeOptions.Codec(e.codec).Label}, {e.resolution}p, quality {e.crf}";

        public static string Percent(double fraction) => (int)Math.Floor(fraction * 100) + "%";

        public static string Percent(long before, long after) => before > 0 ? $"{(before - after) * 100 / before}%" : "";

        public static string Status(EncodeItemInfo i) => i.Status switch
        {
            EncodeItemStatus.Queued => "Waiting",
            EncodeItemStatus.Running => "Encoding " + Percent(i.Fraction),
            EncodeItemStatus.Done => Original(i, i.Saved >= 0 ? $"Done - saved {Fmt.Size(i.Saved)} ({Percent(i.SizeBytes, i.OutputBytes)})" : $"Done - {Fmt.Size(-i.Saved)} bigger"),
            EncodeItemStatus.AlreadyEncoded => Original(i, i.Saved >= 0 ? $"Already encoded - the new file saves {Fmt.Size(i.Saved)} ({Percent(i.SizeBytes, i.OutputBytes)})" : "Already encoded - the new file is bigger"),
            EncodeItemStatus.Incomplete => "New file is incomplete: " + i.Message,
            EncodeItemStatus.Failed => "Failed: " + i.Message,
            _ => "Skipped: " + i.Message,
        };

        private static string Original(EncodeItemInfo i, string text) =>
            text + (i.OriginalDeleted ? ". The original has been deleted" : "") + (i.NewDeleted ? ". The new file has been deleted" : "");

        /// <summary>What the encoder is doing now, in one line.</summary>
        public static string Headline(EncodeProgress p)
        {
            if (p.Error.Length > 0) return "Stopped: " + p.Error;
            if (p.Running)
            {
                int total = p.FinishedCount + p.PendingCount;
                return $"Encoding video {Math.Min(p.FinishedCount + 1, total):N0} of {total:N0}";
            }
            if (p.QueuedCount > 0) return p.Halted ? $"Stopped - {Fmt.Plural(p.QueuedCount, "video", "videos")} waiting" : "Starting...";
            if (p.FinishedCount > 0) return p.FailedCount + p.IncompleteCount > 0 ? "Finished, with problems" : "All done";
            return "Nothing in the queue";
        }

        public static string Left(EncodeProgress p) =>
            p.Running && p.Remaining is TimeSpan r ? Fmt.Approx(r) + " left" : p.Running ? "Estimating time left..." : "";

        public static string CurrentDetail(EncodeItemInfo i)
        {
            var parts = new List<string> { Percent(i.Fraction) };
            if (i.Speed > 0) parts.Add($"{i.Speed:0.0}x speed");
            if (i.Fps > 0) parts.Add($"{i.Fps:0} fps");
            if (i.OutputBytes > 0) parts.Add(Fmt.Size(i.OutputBytes) + " so far");
            if (i.Remaining is TimeSpan r) parts.Add(Fmt.Approx(r) + " left");
            return string.Join(" - ", parts);
        }

        public static string Totals(EncodeProgress p)
        {
            var parts = new List<string> { $"{p.DoneCount:N0} done" };
            if (p.AlreadyEncodedCount > 0) parts.Add($"{p.AlreadyEncodedCount:N0} already encoded");
            if (p.IncompleteCount > 0) parts.Add($"{p.IncompleteCount:N0} incomplete");
            if (p.QueuedCount > 0) parts.Add($"{p.QueuedCount:N0} waiting");
            if (p.FailedCount > 0) parts.Add($"{p.FailedCount:N0} failed");
            if (p.SkippedCount > 0) parts.Add($"{p.SkippedCount:N0} skipped");
            return string.Join(", ", parts);
        }

        public static string Saved(EncodeProgress p) =>
            p.DoneOriginalBytes == 0 ? "" : $"{Fmt.Size(p.DoneOriginalBytes)} became {Fmt.Size(p.DoneNewBytes)} - saved {Fmt.Size(p.SavedBytes)} ({Percent(p.DoneOriginalBytes, p.DoneNewBytes)})";
    }
}

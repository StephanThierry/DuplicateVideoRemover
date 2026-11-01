using System;

namespace deepduplicates
{
    public enum ScanPhase { Idle, Preparing, FindingFiles, ReadingVideos, Comparing, Done, Cancelled, Failed }

    /// <summary>A snapshot of a running scan, reported to the interfaces.</summary>
    public record ScanProgress
    {
        public ScanPhase Phase { get; init; }
        public string Message { get; init; } = "";

        /// <summary>Video files found in the scope (counts up while finding files).</summary>
        public int FilesFound { get; init; }

        /// <summary>Files checked so far in the reading phase (cached + new).</summary>
        public int Checked { get; init; }

        /// <summary>Files that need reading (not in the database yet, or incomplete there).</summary>
        public int NewTotal { get; init; }
        public int NewDone { get; init; }
        public int Failed { get; init; }

        public string CurrentFile { get; init; } = "";

        /// <summary>Database id of the last video that got screenshots (for live thumbnails).</summary>
        public int? LastScreenshotId { get; init; }

        public TimeSpan Elapsed { get; init; }

        /// <summary>Estimated time left of the reading phase, when it can be estimated.</summary>
        public TimeSpan? Remaining { get; init; }

        /// <summary>Seconds per new file used for the estimate.</summary>
        public double? SecondsPerNewFile { get; init; }

        /// <summary>True when the estimate is still based on earlier runs, not this one.</summary>
        public bool EstimateFromHistory { get; init; }

        // A scan has two phases, each with its own progress:
        //   A) finding the files - walking the folders. The total is not known yet, so it is measured against
        //      the number of files the last scan of the same folders found.
        //   B) processing - all files are known now, and which ones are new and need reading.

        /// <summary>How many video files the last scan of the same folders found (null: never scanned).</summary>
        public int? ExpectedFiles { get; init; }

        /// <summary>True once the file list is complete (phase B has started).</summary>
        public bool FilesListed { get; init; }

        /// <summary>How long finding took, once it is done.</summary>
        public TimeSpan? FindTime { get; init; }

        /// <summary>Estimated time left of finding (from the pace so far, or the last scan's timing).</summary>
        public TimeSpan? FindRemaining { get; init; }

        /// <summary>Phase A, 0..1: files found against the number expected from the last scan.</summary>
        public double FindFraction =>
            FilesListed || Phase == ScanPhase.Done ? 1
            : Phase == ScanPhase.FindingFiles && ExpectedFiles > 0 ? Math.Min(FilesFound / (double)ExpectedFiles.Value, 0.99)
            : 0;

        /// <summary>Phase B, 0..1: new videos read of those that need reading (or files checked, when none are new).</summary>
        public double ProcessFraction => Phase switch
        {
            ScanPhase.Done => 1,
            ScanPhase.Comparing => ReadShare + (1 - ReadShare) * CompareFraction,
            ScanPhase.ReadingVideos => ReadShare * ReadFraction,
            _ => 0
        };

        // Phase B has two steps: 1) reading the new videos, 2) comparing all of them (about a minute for
        // 70,000 videos). ReadShare is the part of the B bar for step 1, set from the expected time of each
        // step when the file list is complete, so the bar keeps moving through both.

        /// <summary>Part of the processing bar for reading (the rest is comparing).</summary>
        public double ReadShare { get; init; } = 0.5;

        /// <summary>Step 1, 0..1: new videos read (or files checked, when none are new).</summary>
        public double ReadFraction =>
            NewTotal > 0 ? NewDone / (double)NewTotal : FilesFound > 0 ? Checked / (double)FilesFound : 0;

        /// <summary>Step 2, 0..1: pairs of videos compared.</summary>
        public double CompareFraction { get; init; }

        /// <summary>Expected time of comparing (the last scan's, or a default by number of videos).</summary>
        public TimeSpan? CompareEstimate { get; init; }

        /// <summary>Estimated time left of comparing, while comparing.</summary>
        public TimeSpan? CompareRemaining { get; init; }
    }

    public enum PhaseState { Waiting, Active, Done }

    /// <summary>The texts shown with the two progress bars, shared by the web and desktop apps.</summary>
    public static class ScanText
    {
        // --- A) Finding the files ---

        public static PhaseState FindState(ScanProgress p) =>
            p.FilesListed || p.Phase == ScanPhase.Done ? PhaseState.Done
            : p.Phase is ScanPhase.Preparing or ScanPhase.FindingFiles ? PhaseState.Active
            : PhaseState.Waiting;

        /// <summary>True while finding with nothing to measure against (first scan of these folders).</summary>
        public static bool FindUnknownTotal(ScanProgress p) => FindState(p) == PhaseState.Active && !(p.ExpectedFiles > 0);

        public static string FindDetail(ScanProgress p)
        {
            if (p.Phase == ScanPhase.Preparing) return "Getting ready (checking FFmpeg)";
            if (FindState(p) == PhaseState.Done)
                return $"{Fmt.Plural(p.FilesFound, "video", "videos")} found" + (p.FindTime is TimeSpan t ? $" in {Fmt.Time(t)}" : "");
            if (p.Phase != ScanPhase.FindingFiles) return "";
            return p.ExpectedFiles > 0
                ? $"{p.FilesFound:N0} of about {p.ExpectedFiles.Value:N0} videos (as many as last time)"
                : $"{Fmt.Plural(p.FilesFound, "video", "videos")} found - first scan of these folders, so the total isn't known yet";
        }

        public static string FindLeft(ScanProgress p)
        {
            if (p.Phase != ScanPhase.FindingFiles) return "";
            if (p.ExpectedFiles > 0 && p.FilesFound >= p.ExpectedFiles) return "Almost done";
            if (p.FindRemaining is not TimeSpan r) return "";
            return Fmt.Approx(r) + " left" + (p.EstimateFromHistory ? " (from the last scan)" : "");
        }

        // --- B) Processing the files ---

        public static PhaseState ProcessState(ScanProgress p) =>
            p.Phase == ScanPhase.Done ? PhaseState.Done
            : p.Phase is ScanPhase.ReadingVideos or ScanPhase.Comparing ? PhaseState.Active
            : PhaseState.Waiting;

        public static string ProcessDetail(ScanProgress p)
        {
            switch (p.Phase)
            {
                case ScanPhase.Done: return "Done";
                case ScanPhase.Comparing:
                    return $"Step 2 of 2: comparing {p.FilesFound:N0} videos - {Percent(p.CompareFraction)}% compared";
                case ScanPhase.ReadingVideos when p.Checked == 0 && p.NewTotal == 0: return p.Message;
                case ScanPhase.ReadingVideos when p.NewTotal > 0:
                    return $"Step 1 of 2: reading new videos - {p.NewDone:N0} of {p.NewTotal:N0} read, {Math.Max(p.FilesFound - p.NewTotal, 0):N0} already known";
                case ScanPhase.ReadingVideos: return $"Step 1 of 2: no new videos - all {p.FilesFound:N0} were read before";
                default: return p.FilesListed ? "" : "Starts when all files are found";
            }
        }

        /// <summary>Time left of the whole processing phase: reading what is left, then comparing.</summary>
        public static string ProcessLeft(ScanProgress p)
        {
            if (p.Phase == ScanPhase.Comparing)
                return p.CompareRemaining is TimeSpan c ? Fmt.Approx(c) + " left" : "";
            if (p.Phase != ScanPhase.ReadingVideos || (p.Checked == 0 && p.NewTotal == 0)) return "";
            TimeSpan compare = p.CompareEstimate ?? TimeSpan.Zero;
            if (p.NewTotal == 0) return compare > TimeSpan.Zero ? Fmt.Approx(compare) + " left (comparing)" : "";
            if (p.Remaining is not TimeSpan r) return "Estimating time left...";
            return Fmt.Approx(r + compare) + " left" + (p.EstimateFromHistory ? " (from earlier scans)" : "");
        }

        public static int Percent(double fraction) => (int)Math.Floor(fraction * 100);
    }
}

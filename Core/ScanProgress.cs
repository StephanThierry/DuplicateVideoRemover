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
            ScanPhase.Done or ScanPhase.Comparing => 1,
            ScanPhase.ReadingVideos when NewTotal > 0 => NewDone / (double)NewTotal,
            ScanPhase.ReadingVideos when FilesFound > 0 => Checked / (double)FilesFound,
            _ => 0
        };
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
                case ScanPhase.Comparing: return "Comparing videos";
                case ScanPhase.ReadingVideos when p.Checked == 0 && p.NewTotal == 0: return p.Message;
                case ScanPhase.ReadingVideos when p.NewTotal > 0:
                    return $"{p.NewDone:N0} of {Fmt.Plural(p.NewTotal, "new video", "new videos")} read, {Math.Max(p.FilesFound - p.NewTotal, 0):N0} already known";
                case ScanPhase.ReadingVideos: return $"No new videos - all {p.FilesFound:N0} were read before";
                default: return p.FilesListed ? "" : "Starts when all files are found";
            }
        }

        public static string ProcessLeft(ScanProgress p)
        {
            if (p.Phase != ScanPhase.ReadingVideos || p.NewTotal == 0) return "";
            if (p.Remaining is not TimeSpan r) return "Estimating time left...";
            return Fmt.Approx(r) + " left" + (p.EstimateFromHistory ? " (from earlier scans)" : "");
        }

        public static int Percent(double fraction) => (int)Math.Floor(fraction * 100);
    }
}

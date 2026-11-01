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

        /// <summary>How many video files the last scan of the same folders found (null: never scanned).</summary>
        public int? ExpectedFiles { get; init; }

        /// <summary>
        /// Part of the progress bar given to finding files, from the last scan's timing. On a large library on
        /// network drives, walking the folders can take as long as everything else.
        /// </summary>
        public double FindShare { get; init; } = RunHistory.DefaultFindShare;

        /// <summary>
        /// Overall progress 0..1. Finding files fills its share by the count expected from the last scan;
        /// reading fills the rest up to 95%.
        /// </summary>
        public double Fraction => Phase switch
        {
            ScanPhase.Done => 1,
            ScanPhase.Preparing => 0.01,
            ScanPhase.FindingFiles when ExpectedFiles > 0 => Math.Max(0.02, FindShare * Math.Min(FilesFound / (double)ExpectedFiles.Value, 0.98)),
            ScanPhase.ReadingVideos when NewTotal > 0 => FindShare + (0.95 - FindShare) * NewDone / NewTotal,
            ScanPhase.ReadingVideos when FilesFound > 0 => FindShare + (0.95 - FindShare) * Checked / FilesFound,
            ScanPhase.Comparing => 0.97,
            _ => 0.02
        };
    }
}

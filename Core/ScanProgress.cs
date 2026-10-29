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

        /// <summary>Overall progress 0..1 (finding files is not counted, its total is unknown).</summary>
        public double Fraction => Phase switch
        {
            ScanPhase.Done => 1,
            ScanPhase.ReadingVideos when NewTotal > 0 => 0.05 + 0.9 * NewDone / NewTotal,
            ScanPhase.ReadingVideos when FilesFound > 0 => 0.05 + 0.9 * Checked / FilesFound,
            ScanPhase.Comparing => 0.97,
            _ => 0.02
        };
    }
}

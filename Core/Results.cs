using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace deepduplicates
{
    /// <summary>One video in the results.</summary>
    public sealed class VideoEntry
    {
        public int Id { get; set; }
        public string Path { get; set; } = "";
        public long SizeBytes { get; set; }
        public int DurationSeconds { get; set; }
        public double Bitrate { get; set; }
        /// <summary>Why the engine suggests removing it (match score, broken, too short ...).</summary>
        public string Reason { get; set; } = "";
        /// <summary>A live picture on the Scan page: the video was read for the first time in this scan (not already in the database).</summary>
        public bool IsNew { get; set; }

        [System.Text.Json.Serialization.JsonIgnore] public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
        [System.Text.Json.Serialization.JsonIgnore] public string FileName => System.IO.Path.GetFileName(Path);

        /// <summary>
        /// The bitrate to show and sort by, in bit/s. The one stored in the database is what the video stream's header says, and some
        /// files lie (a .wmv with 4,294,096 kbps - a 32-bit overflow - for 476 MB in 41 minutes; a placeholder of 104,857 kbps). A stored value
        /// far from the average (size / length, which includes the sound) is replaced by that average.
        /// </summary>
        public static double SaneBitrate(double stored, long sizeBytes, int durationSeconds)
        {
            double average = durationSeconds > 0 && sizeBytes > 0 ? sizeBytes * 8.0 / durationSeconds : 0;
            if (average <= 0) return stored > 0 && stored < 1e9 ? stored : 0;      // no length: nothing to check against, but not an overflow
            return stored >= average * 0.5 && stored <= average * 1.5 ? stored : average;
        }

        public static VideoEntry From(VideoInfo v) => new VideoEntry
        {
            Id = v.id,
            Path = v.path ?? "",
            SizeBytes = Math.Max(v.fileSize ?? 0, 0),
            DurationSeconds = v.duration ?? -1,
            Bitrate = SaneBitrate(v.bitrate ?? 0, Math.Max(v.fileSize ?? 0, 0), v.duration ?? -1),
            Reason = v.reason ?? ""
        };
    }

    /// <summary>Videos that are copies of each other. The engine's choice of which to keep is listed first.</summary>
    public sealed class DuplicateGroup
    {
        public int Id { get; set; }
        public List<VideoEntry> Members { get; set; } = new();
        /// <summary>The copy the engine suggests keeping.</summary>
        public int SuggestedKeepId { get; set; }
    }

    /// <summary>Everything a scan or analysis found.</summary>
    public sealed class ResultSet
    {
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public List<DuplicateGroup> Groups { get; set; } = new();
        /// <summary>Broken, incomplete, too short or too small videos (no original to compare with).</summary>
        public List<VideoEntry> Broken { get; set; } = new();
        public int VideosInScope { get; set; }

        [System.Text.Json.Serialization.JsonIgnore] public int DuplicateCount => Groups.Sum(g => g.Members.Count - 1);
        [System.Text.Json.Serialization.JsonIgnore] public long DuplicateBytes => Groups.Sum(g => g.Members.Where(m => m.Id != g.SuggestedKeepId).Sum(m => m.SizeBytes));

        public static string FilePath => System.IO.Path.Combine(FileHandler.AppFolder, "output", "results.json");

        /// <summary>Saves to output/results.json (paths in database form, so the file works on both OSes).</summary>
        public void Save()
        {
            var copy = JsonSerializer.Deserialize<ResultSet>(JsonSerializer.Serialize(this));
            foreach (var e in copy.AllEntries()) e.Path = PathMapper.ToStorage(e.Path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(copy));
        }

        public static ResultSet LoadSaved()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var set = JsonSerializer.Deserialize<ResultSet>(File.ReadAllText(FilePath));
                foreach (var e in set.AllEntries()) e.Path = PathMapper.ToLocal(e.Path);
                return set;
            }
            catch (Exception e)
            {
                Console.WriteLine("Could not read saved results: " + e.Message);
                return null;
            }
        }

        public IEnumerable<VideoEntry> AllEntries() => Groups.SelectMany(g => g.Members).Concat(Broken);
    }
}

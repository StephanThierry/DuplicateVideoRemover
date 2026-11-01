using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace deepduplicates
{
    /// <summary>One finished (or stopped) scan, kept in runhistory.json for time estimates.</summary>
    public sealed class RunRecord
    {
        public string ScopeKey { get; set; } = "";
        public string Machine { get; set; } = "";
        public DateTime StartedUtc { get; set; }
        public double TotalSeconds { get; set; }
        public int FilesInScope { get; set; }
        public int NewFiles { get; set; }
        public double NewFileSeconds { get; set; }
        public bool Completed { get; set; }
        /// <summary>Seconds spent finding the files (walking the folders), for the progress bar.</summary>
        public double FindSeconds { get; set; }
    }

    public static class RunHistory
    {
        // Next to the database: the timings belong to the videos read into it
        private static string FilePath => Path.Combine(DataLocation.DatabaseFolder, "runhistory.json");

        /// <summary>Identifies a scan scope: the content folders, the same on Windows and Linux.</summary>
        public static string ScopeKey(Settings settings)
        {
            var folders = settings.contentFolders
                .Select(f => PathMapper.ToStorage(f).TrimEnd('\\', '/').ToLowerInvariant())
                .OrderBy(f => f);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", folders)));
            return Convert.ToHexString(hash, 0, 8);
        }

        public static List<RunRecord> Load()
        {
            try
            {
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<RunRecord>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch { return new(); }
        }

        public static void Add(RunRecord record)
        {
            var all = Load();
            all.Add(record);
            // Keep it small
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all.TakeLast(200).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        /// Average seconds per new video from earlier runs. Prefers this scope on this machine,
        /// then any scope on this machine. Null when there is no history.
        /// </summary>
        public static double? SecondsPerNewFile(Settings settings)
        {
            var useful = Load().Where(r => r.NewFiles >= 5 && r.Machine == Environment.MachineName).ToList();
            var scope = ScopeKey(settings);
            var pick = useful.Where(r => r.ScopeKey == scope).TakeLast(5).ToList();
            if (pick.Count == 0) pick = useful.TakeLast(5).ToList();
            if (pick.Count == 0) return null;
            return pick.Sum(r => r.NewFileSeconds) / pick.Sum(r => r.NewFiles);
        }

        /// <summary>Share of the progress bar for finding files when there is no timing yet.</summary>
        public const double DefaultFindShare = 0.25;

        /// <summary>
        /// Part of the last completed scan of this scope spent finding files (5-60%), for the progress bar.
        /// </summary>
        public static double FindShare(Settings settings)
        {
            var scope = ScopeKey(settings);
            var last = Load().LastOrDefault(r => r.ScopeKey == scope && r.Completed && r.FindSeconds > 0 && r.TotalSeconds > 0);
            return last == null ? DefaultFindShare : Math.Clamp(last.FindSeconds / last.TotalSeconds, 0.05, 0.6);
        }

        /// <summary>How many files the last scan of this scope found, or null.</summary>
        public static int? ExpectedFiles(Settings settings)
        {
            var scope = ScopeKey(settings);
            var last = Load().LastOrDefault(r => r.ScopeKey == scope && r.Completed && r.FilesInScope > 0);
            return last?.FilesInScope;
        }

        public static RunRecord LastRun(Settings settings)
        {
            var scope = ScopeKey(settings);
            return Load().LastOrDefault(r => r.ScopeKey == scope);
        }
    }
}

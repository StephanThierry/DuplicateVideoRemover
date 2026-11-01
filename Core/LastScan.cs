using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace deepduplicates
{
    /// <summary>
    /// The video files the last scan on this installation found on disk (output/lastscan.json).
    ///
    /// The database is NOT a list of existing files: it keeps every file ever read, including "dormant" rows
    /// for files that were moved, renamed, deleted or are on a drive that isn't reachable right now. It is
    /// only a cache ("I've read this exact file before"). Anything that needs the files that exist now -
    /// Compare again, Re-encode - takes the database rows whose paths are in this list.
    /// See TECHNICAL.md.
    /// </summary>
    public static class LastScan
    {
        private sealed class FileData
        {
            public DateTime FoundUtc { get; set; }
            public List<string> Paths { get; set; } = new();
        }

        private static readonly object sync = new();
        private static HashSet<string> paths;
        private static string loadedFrom;
        private static DateTime foundUtc;

        private static string FilePath => Path.Combine(FileHandler.AppFolder, "output", "lastscan.json");

        private static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>Saves the files a scan found (called by the scan once it has listed the folders).</summary>
        public static void Save(IEnumerable<string> found)
        {
            var list = found.ToList();
            lock (sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new FileData { FoundUtc = DateTime.UtcNow, Paths = list }));
                File.Move(tmp, FilePath, overwrite: true);
                foundUtc = DateTime.UtcNow;
                paths = new HashSet<string>(list, Comparer);
                loadedFrom = FilePath;
            }
        }

        /// <summary>Forgets a file (deleted by the app).</summary>
        public static void Remove(string path)
        {
            lock (sync) { EnsureLoaded(); paths?.Remove(path); }
        }

        /// <summary>When the last scan listed the files (UTC), or null when there is no list.</summary>
        public static DateTime? FoundUtc { get { lock (sync) { EnsureLoaded(); return paths == null ? null : foundUtc; } } }

        /// <summary>The entries whose path is in the list (null when there is no list).</summary>
        public static List<VideoEntry> FilterEntries(IEnumerable<VideoEntry> entries)
        {
            HashSet<string> set;
            lock (sync) { EnsureLoaded(); set = paths; }
            return set == null ? null : entries.Where(e => set.Contains(e.Path)).ToList();
        }

        /// <summary>True when a scan list exists on this installation.</summary>
        public static bool Exists { get { lock (sync) { EnsureLoaded(); return paths != null; } } }

        /// <summary>
        /// The videos that the last scan found on disk. Without a scan list yet (first start after an update),
        /// the videos whose file exists now.
        /// </summary>
        public static List<VideoInfo> Filter(IEnumerable<VideoInfo> media)
        {
            HashSet<string> set;
            lock (sync) { EnsureLoaded(); set = paths; }
            if (set != null) return media.Where(v => v.path != null && set.Contains(v.path)).ToList();
            return OnlyExisting(media);
        }

        /// <summary>The videos whose file exists right now.</summary>
        public static List<VideoInfo> OnlyExisting(IEnumerable<VideoInfo> media)
        {
            var list = media.Where(v => v.path != null).ToList();
            var exists = Exist(list.Select(v => v.path).ToList());
            return list.Where((v, i) => exists[i]).ToList();
        }

        /// <summary>Whether each file exists right now (checked in parallel - shares can be slow).</summary>
        public static bool[] Exist(IReadOnlyList<string> files)
        {
            var exists = new bool[files.Count];
            System.Threading.Tasks.Parallel.For(0, files.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 32 },
                i => { try { exists[i] = File.Exists(files[i]); } catch { } });
            return exists;
        }

        private static void EnsureLoaded()
        {
            if (loadedFrom == FilePath) return;
            loadedFrom = FilePath;
            paths = null;
            try
            {
                if (!File.Exists(FilePath)) return;
                var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(FilePath));
                if (data != null) { paths = new HashSet<string>(data.Paths, Comparer); foundUtc = data.FoundUtc; }
            }
            catch (Exception e) { Console.WriteLine("Could not read lastscan.json: " + e.Message); }
        }
    }
}

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
        private static HashSet<string> exact;   // the same paths, with exactly the spelling the scan found
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
                paths = new HashSet<string>(list, Comparer); exact = new HashSet<string>(list, StringComparer.Ordinal);
                loadedFrom = FilePath;
            }
        }

        /// <summary>Forgets a file (deleted by the app).</summary>
        public static void Remove(string path)
        {
            lock (sync) { EnsureLoaded(); paths?.Remove(path); exact?.Remove(path); }
        }

        /// <summary>Renames paths in the list (a synonym update changed the names the database uses) and saves it.</summary>
        public static void Rename(Func<string, string> rename)
        {
            lock (sync)
            {
                EnsureLoaded();
                if (paths == null) return;
                var list = paths.Select(rename).ToList();
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new FileData { FoundUtc = foundUtc, Paths = list }));
                File.Move(tmp, FilePath, overwrite: true);
                paths = new HashSet<string>(list, Comparer); exact = new HashSet<string>(list, StringComparer.Ordinal);
            }
        }

        /// <summary>When the last scan listed the files (UTC), or null when there is no list.</summary>
        public static DateTime? FoundUtc { get { lock (sync) { EnsureLoaded(); return paths == null ? null : foundUtc; } } }

        /// <summary>The entries whose path is in the list (null when there is no list).</summary>
        public static List<VideoEntry> FilterEntries(IEnumerable<VideoEntry> entries)
        {
            HashSet<string> set;
            lock (sync) { EnsureLoaded(); set = paths; }
            return set == null ? null : OnePerFile(entries.Where(e => set.Contains(e.Path)), e => e.Path, e => e.Id, e => false);
        }

        /// <summary>
        /// One row per file. On Windows "A\Video.mp4" and "a\video.mp4" are the same file, but the database keeps a
        /// row for each spelling (the scan matches paths exactly): when a folder changes case on disk, the old row stays
        /// as a dormant copy that the list - which ignores case - would let through, and it matches its own twin 100 %.
        /// The row with the spelling the scan found is kept (else the one with screenshots, else the oldest).
        /// </summary>
        private static List<T> OnePerFile<T>(IEnumerable<T> rows, Func<T, string> path, Func<T, int> id, Func<T, bool> hasShots)
        {
            var list = rows.ToList();
            if (Comparer == StringComparer.Ordinal) return list;   // paths that differ in case are different files
            var byPath = new Dictionary<string, List<T>>(Comparer);
            foreach (var r in list)
            {
                if (!byPath.TryGetValue(path(r), out var same)) byPath[path(r)] = same = new List<T>();
                same.Add(r);
            }
            if (byPath.Count == list.Count) return list;
            var keep = new HashSet<T>(byPath.Values.Select(same => same.Count == 1 ? same[0]
                : same.OrderByDescending(r => ExactSpelling(path(r)))
                      .ThenByDescending(hasShots).ThenBy(id).First()));
            return list.Where(keep.Contains).ToList();
        }

        // Whether the list has this exact spelling (the list itself ignores case on Windows)
        private static bool ExactSpelling(string path) => exact != null && exact.Contains(path);

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
            if (set != null) return OnePerFile(media.Where(v => v.path != null && set.Contains(v.path)), v => v.path, v => v.id, HasShots);
            return OnlyExisting(media);
        }

        private static bool HasShots(VideoInfo v) => v.image1hash_blob != null && (v.duration ?? -1) > 0;

        /// <summary>The videos whose file exists right now.</summary>
        public static List<VideoInfo> OnlyExisting(IEnumerable<VideoInfo> media)
        {
            var list = media.Where(v => v.path != null).ToList();
            var exists = Exist(list.Select(v => v.path).ToList());
            return OnePerFile(list.Where((v, i) => exists[i]), v => v.path, v => v.id, HasShots);
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
            paths = null; exact = null;
            try
            {
                if (!File.Exists(FilePath)) return;
                var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(FilePath));
                if (data != null) { paths = new HashSet<string>(data.Paths, Comparer); exact = new HashSet<string>(data.Paths, StringComparer.Ordinal); foundUtc = data.FoundUtc; }
            }
            catch (Exception e) { Console.WriteLine("Could not read lastscan.json: " + e.Message); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace deepduplicates
{
    /// <summary>Two videos the user marked as "not duplicates", recognised by their content, not their paths.</summary>
    public sealed class FalsePositive
    {
        /// <summary>Content fingerprints of the two videos (see FalsePositives.Fingerprint), in sorted order.</summary>
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        /// <summary>Where the files were when they were marked (database form), only for showing them.</summary>
        public string PathA { get; set; } = "";
        public string PathB { get; set; } = "";
        public long SizeA { get; set; }
        public long SizeB { get; set; }
        public int DurationSeconds { get; set; }
        public DateTime MarkedUtc { get; set; }

        [System.Text.Json.Serialization.JsonIgnore] public string Key => A + "|" + B;
    }

    /// <summary>
    /// The pairs of videos marked as false positives, kept in falsepositives.json next to the database
    /// (they belong to the videos read into it, so installations sharing a database share them too).
    ///
    /// A video is recognised by a fingerprint of its content - size, length and the screenshot hashes -
    /// so a mark still applies after the files are moved or renamed and read again by a scan.
    /// </summary>
    public static class FalsePositives
    {
        private static readonly object sync = new();
        private static List<FalsePositive> items;
        private static HashSet<string> keys;
        private static string loadedFrom;

        private static string FilePath => Path.Combine(DataLocation.DatabaseFolder, "falsepositives.json");

        /// <summary>Raised after a pair is added or removed.</summary>
        public static event Action Changed;

        /// <summary>A path-independent fingerprint of a video: size, length and screenshot hashes.</summary>
        public static string Fingerprint(VideoInfo v)
        {
            using var sha = SHA256.Create();
            void Add(byte[] bytes) { if (bytes != null) sha.TransformBlock(bytes, 0, bytes.Length, null, 0); }
            void AddText(string s) => Add(Encoding.UTF8.GetBytes((s ?? "") + "\n"));
            AddText(v.fileSize?.ToString() ?? "");
            AddText(v.duration?.ToString() ?? "");
            Add(v.image1hash_blob); Add(v.image2hash_blob); Add(v.image3hash_blob);
            // Older rows may have only the colour checksums
            if (v.image1hash_blob == null) { AddText(v.image1Checksum_json); AddText(v.image2Checksum_json); AddText(v.image3Checksum_json); }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash, 0, 16);
        }

        public static List<FalsePositive> All()
        {
            lock (sync) { EnsureLoaded(); return items.OrderByDescending(i => i.MarkedUtc).ToList(); }
        }

        public static int Count { get { lock (sync) { EnsureLoaded(); return items.Count; } } }

        /// <summary>True when the two fingerprints were marked as not duplicates.</summary>
        public static bool IsPair(string a, string b)
        {
            if (a == null || b == null || a == b) return false;
            lock (sync)
            {
                EnsureLoaded();
                return keys.Count > 0 && keys.Contains(KeyOf(a, b));
            }
        }

        /// <summary>Marks one video as not a duplicate of each of the others. Returns how many pairs were new.</summary>
        public static int Add(VideoInfo video, IEnumerable<VideoInfo> others)
        {
            int added = 0;
            lock (sync)
            {
                EnsureLoaded();
                string fv = Fingerprint(video);
                foreach (var o in others)
                {
                    string fo = Fingerprint(o);
                    if (fo == fv) continue;
                    bool first = string.CompareOrdinal(fv, fo) < 0;
                    var (x, y) = first ? (video, o) : (o, video);
                    var item = new FalsePositive
                    {
                        A = first ? fv : fo,
                        B = first ? fo : fv,
                        PathA = PathMapper.ToStorage(x.path ?? ""),
                        PathB = PathMapper.ToStorage(y.path ?? ""),
                        SizeA = x.fileSize ?? 0,
                        SizeB = y.fileSize ?? 0,
                        DurationSeconds = video.duration ?? -1,
                        MarkedUtc = DateTime.UtcNow
                    };
                    if (!keys.Add(item.Key)) continue;
                    items.Add(item);
                    added++;
                }
                if (added > 0) Save();
            }
            if (added > 0) Changed?.Invoke();
            return added;
        }

        /// <summary>Forgets a pair: the two videos can be found as duplicates again.</summary>
        public static bool Remove(string key)
        {
            bool removed;
            lock (sync)
            {
                EnsureLoaded();
                removed = items.RemoveAll(i => i.Key == key) > 0;
                if (removed) { keys.Remove(key); Save(); }
            }
            if (removed) Changed?.Invoke();
            return removed;
        }

        private static string KeyOf(string a, string b) => string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;

        private static void EnsureLoaded()
        {
            // The database folder is set once at start-up; reload if it changed (tests, tools)
            if (items != null && loadedFrom == FilePath) return;
            loadedFrom = FilePath;
            try
            {
                items = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<FalsePositive>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch (Exception e)
            {
                Console.WriteLine("Could not read falsepositives.json: " + e.Message);
                items = new();
            }
            keys = items.Select(i => i.Key).ToHashSet();
        }

        private static void Save()
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }
}

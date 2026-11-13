#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace deepduplicates
{
    /// <summary>
    /// Folders hidden in Re-encode planning (by "Exclude folder" on a video, or added on the Manage hidden folders screen).
    /// A hidden folder hides its subfolders too, so a parent folder can be hidden in one go. Kept in output/hiddenfolders.json,
    /// so they stay hidden after a restart - and the desktop app and the web interface on the same data folder share them.
    /// Only hides videos from the list; nothing is skipped in scans (that is Settings' paths to skip).
    /// </summary>
    public sealed class HiddenFolders
    {
        private readonly string file;
        private readonly object sync = new();
        private List<string> folders = new();

        /// <summary>Raised after a change (from any thread).</summary>
        public event Action? Changed;

        public HiddenFolders(string file)
        {
            this.file = file;
            try
            {
                if (File.Exists(file)) folders = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? new();
            }
            catch (Exception e) { Console.WriteLine("Could not read the hidden folders: " + e.Message); }
            folders = folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(Clean).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The hidden folders, sorted.</summary>
        public IReadOnlyList<string> List { get { lock (sync) return folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); } }

        public int Count { get { lock (sync) return folders.Count; } }

        /// <summary>True when this folder or one above it is hidden.</summary>
        public bool Hides(string folder)
        {
            lock (sync) return folders.Any(h => Covers(h, folder));
        }

        /// <summary>True when <paramref name="hidden"/> is <paramref name="folder"/> or one of the folders above it.</summary>
        public static bool Covers(string hidden, string folder)
        {
            string h = Key(hidden), f = Key(folder);
            return f.Equals(h, StringComparison.OrdinalIgnoreCase) || (h.Length > 0 && f.StartsWith(h + "/", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Hides a folder (and what is below it). False when it was hidden already.</summary>
        public bool Add(string folder)
        {
            folder = Clean(folder);
            if (folder.Length == 0) return false;
            lock (sync)
            {
                if (folders.Any(h => Key(h).Equals(Key(folder), StringComparison.OrdinalIgnoreCase))) return false;
                folders.Add(folder);
                Save();
            }
            Changed?.Invoke();
            return true;
        }

        public bool Remove(string folder)
        {
            lock (sync)
            {
                if (folders.RemoveAll(h => Key(h).Equals(Key(folder), StringComparison.OrdinalIgnoreCase)) == 0) return false;
                Save();
            }
            Changed?.Invoke();
            return true;
        }

        public void Clear()
        {
            lock (sync)
            {
                if (folders.Count == 0) return;
                folders.Clear();
                Save();
            }
            Changed?.Invoke();
        }

        // As typed or pasted, without quotes and a trailing separator (but "C:\" and "/" stay what they are)
        private static string Clean(string folder)
        {
            string f = folder.Trim().Trim('"').Trim();
            while (f.Length > 1 && (f.EndsWith('/') || f.EndsWith('\\')) && !(f.Length == 3 && f[1] == ':')) f = f[..^1];
            return f;
        }

        // For comparing: one kind of separator, no trailing one
        private static string Key(string folder) => Clean(folder).Replace('\\', '/').TrimEnd('/');

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, JsonSerializer.Serialize(folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) { Console.WriteLine("Could not save the hidden folders: " + e.Message); }
        }
    }
}

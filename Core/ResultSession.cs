using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace deepduplicates
{
    public sealed record ExecuteFailure(VideoEntry Entry, string Error);

    public sealed class ExecuteReport
    {
        public List<VideoEntry> Removed { get; } = new();
        public List<ExecuteFailure> Failed { get; } = new();
        public long BytesFreed => Removed.Sum(r => r.SizeBytes);
    }

    /// <summary>
    /// The review-and-delete state behind both interfaces: which copy each group keeps, which files are
    /// selected, and deleting them.
    ///
    /// Safety rule: every duplicate group always keeps at least one file that is not selected.
    /// </summary>
    public sealed class ResultSession
    {
        private readonly object sync = new();
        private readonly Dictionary<int, int> keep = new();          // group id -> id of the copy kept
        private readonly HashSet<int> selected = new();
        private readonly Dictionary<int, DuplicateGroup> groupOf = new(); // entry id -> group

        public ResultSet Results { get; private set; }

        /// <summary>Raised after any change to the selection or the results.</summary>
        public event Action Changed;

        public ResultSession(ResultSet results)
        {
            Results = results ?? new ResultSet();
            foreach (var g in Results.Groups)
            {
                keep[g.Id] = g.SuggestedKeepId;
                foreach (var m in g.Members) groupOf[m.Id] = g;
            }
        }

        // --- Reading state ---

        public bool IsSelected(int id) { lock (sync) return selected.Contains(id); }
        public int KeepId(DuplicateGroup g) { lock (sync) return keep.TryGetValue(g.Id, out var k) ? k : g.SuggestedKeepId; }
        public bool IsKept(VideoEntry e) { lock (sync) return groupOf.TryGetValue(e.Id, out var g) && keep[g.Id] == e.Id; }

        public int SelectedCount { get { lock (sync) return selected.Count; } }
        public long SelectedBytes { get { lock (sync) return Results.AllEntries().Where(e => selected.Contains(e.Id)).Sum(e => e.SizeBytes); } }
        public List<VideoEntry> SelectedEntries() { lock (sync) return Results.AllEntries().Where(e => selected.Contains(e.Id)).ToList(); }

        /// <summary>Folders that hold duplicates or broken files, with how many and their size.</summary>
        public List<FolderSummary> Folders()
        {
            lock (sync)
            {
                var removable = Results.Groups.SelectMany(g => g.Members.Where(m => m.Id != keep[g.Id])).Concat(Results.Broken);
                return removable.GroupBy(e => e.Folder, StringComparer.Ordinal)
                    .Select(f => new FolderSummary(f.Key, f.Count(), f.Sum(e => e.SizeBytes), f.Count(e => selected.Contains(e.Id))))
                    .OrderByDescending(f => f.Count).ThenBy(f => f.Folder)
                    .ToList();
            }
        }

        // --- Selecting ---

        /// <summary>Selects every copy the engine suggests removing (all but the kept one in each group).</summary>
        public void SelectAllDuplicates()
        {
            Change(() =>
            {
                foreach (var g in Results.Groups)
                    foreach (var m in g.Members) if (m.Id != keep[g.Id]) selected.Add(m.Id);
            });
        }

        public void SelectAllBroken() => Change(() => { foreach (var b in Results.Broken) selected.Add(b.Id); });

        public void SelectNone() => Change(() => selected.Clear());

        /// <summary>Selects or unselects one file. Returns a message when the safety rule prevents it.</summary>
        public string Toggle(int id) => SetSelected(id, !IsSelected(id));

        public string SetSelected(int id, bool select)
        {
            string message = null;
            Change(() =>
            {
                if (!select) { selected.Remove(id); return; }
                if (groupOf.TryGetValue(id, out var g))
                {
                    var others = g.Members.Where(m => m.Id != id && !selected.Contains(m.Id)).ToList();
                    if (others.Count == 0)
                    {
                        message = "Every copy in this group would be removed. Keep at least one.";
                        return;
                    }
                    // Selecting the kept copy: keep another one instead
                    if (keep[g.Id] == id) keep[g.Id] = others[0].Id;
                }
                selected.Add(id);
            });
            return message;
        }

        /// <summary>Keeps this copy instead of the current one (it is unselected; the others are left as they are).</summary>
        public void KeepInstead(int id)
        {
            Change(() =>
            {
                if (!groupOf.TryGetValue(id, out var g)) return;
                keep[g.Id] = id;
                selected.Remove(id);
            });
        }

        /// <summary>
        /// "Delete all duplicates in this folder": selects every duplicate or broken file in the folder
        /// (not sub folders). A group whose copies are all in this folder keeps one of them.
        /// </summary>
        public int SelectFolder(string folder)
        {
            int count = 0;
            Change(() =>
            {
                foreach (var g in Results.Groups)
                {
                    var inFolder = g.Members.Where(m => m.Folder == folder).ToList();
                    if (inFolder.Count == 0) continue;
                    var outside = g.Members.Where(m => m.Folder != folder && !selected.Contains(m.Id)).ToList();
                    if (outside.Count > 0)
                    {
                        // A copy outside the folder survives: keep that one, select everything in the folder
                        if (inFolder.Any(m => m.Id == keep[g.Id])) keep[g.Id] = outside[0].Id;
                        foreach (var m in inFolder) count += selected.Add(m.Id) ? 1 : 0;
                    }
                    else
                    {
                        // All copies are here: keep the current choice (or the first), select the rest
                        if (!inFolder.Any(m => m.Id == keep[g.Id])) keep[g.Id] = inFolder[0].Id;
                        foreach (var m in inFolder.Where(m => m.Id != keep[g.Id])) count += selected.Add(m.Id) ? 1 : 0;
                    }
                }
                foreach (var b in Results.Broken.Where(b => b.Folder == folder)) count += selected.Add(b.Id) ? 1 : 0;
            });
            return count;
        }

        public void UnselectFolder(string folder) =>
            Change(() => { foreach (var e in Results.AllEntries().Where(e => e.Folder == folder)) selected.Remove(e.Id); });

        // --- Removing ---

        /// <summary>
        /// Deletes the selected files, takes them out of the database and the results,
        /// and saves the results. Files that fail stay selected and are reported.
        /// </summary>
        public ExecuteReport Execute(FileHandler fileHandler)
        {
            var report = new ExecuteReport();
            List<VideoEntry> targets;
            lock (sync) targets = Results.AllEntries().Where(e => selected.Contains(e.Id)).ToList();

            foreach (var entry in targets)
            {
                try
                {
                    if (File.Exists(entry.Path)) File.Delete(entry.Path);
                    // A file that is already gone counts as removed, so it disappears from the list
                    report.Removed.Add(entry);
                }
                catch (Exception e)
                {
                    report.Failed.Add(new ExecuteFailure(entry, e.Message));
                }
            }

            RemoveFromDatabase(report.Removed, fileHandler);

            Change(() =>
            {
                var gone = report.Removed.Select(r => r.Id).ToHashSet();
                foreach (var id in gone) selected.Remove(id);
                Results.Broken.RemoveAll(b => gone.Contains(b.Id));
                foreach (var g in Results.Groups) g.Members.RemoveAll(m => gone.Contains(m.Id));
                // A group with one file left has no duplicates any more
                foreach (var g in Results.Groups.Where(g => g.Members.Count < 2).ToList())
                {
                    Results.Groups.Remove(g);
                    keep.Remove(g.Id);
                    foreach (var m in g.Members) groupOf.Remove(m.Id);
                }
                foreach (var id in gone) groupOf.Remove(id);
                foreach (var g in Results.Groups) g.SuggestedKeepId = keep[g.Id];
            });
            try { Results.Save(); } catch (Exception e) { Console.WriteLine("Could not save results: " + e.Message); }
            return report;
        }

        private static void RemoveFromDatabase(List<VideoEntry> removed, FileHandler fileHandler)
        {
            if (removed.Count == 0) return;
            try
            {
                using var db = new VideoInfoContext();
                var ids = removed.Select(r => r.Id).ToHashSet();
                var rows = db.VideoInfos.Where(v => ids.Contains(v.id)).ToList();
                db.VideoInfos.RemoveRange(rows);
                db.SaveChanges();
                foreach (var row in rows)
                {
                    for (int i = 1; i <= 3; i++)
                    {
                        string shot = fileHandler.screenshotPath(row, i);
                        if (File.Exists(shot)) File.Delete(shot);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Could not update the database: " + e.Message);
            }
        }

        private void Change(Action action)
        {
            lock (sync) action();
            Changed?.Invoke();
        }
    }

    public sealed record FolderSummary(string Folder, int Count, long Bytes, int SelectedCount);
}

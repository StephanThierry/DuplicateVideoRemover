using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace deepduplicates
{
    /// <summary>What "Update paths" did.</summary>
    public sealed record SynonymUpdateReport(int Updated, int Merged, string Message);

    /// <summary>
    /// Synonyms: checking how many videos in the database use an alias (e.g. "L:\"), whether they are found
    /// under the main name (e.g. "\\nas\video\"), and rewriting them to the main name once.
    /// </summary>
    public static class SynonymTools
    {
        public const int SampleSize = 10;

        private sealed record Row(int Id, string Path, bool HasShots);

        private static List<Row> ReadRows()
        {
            using var db = new VideoInfoContext();
            var conn = db.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, path, image1hash_blob IS NOT NULL AND IFNULL(duration, -1) > 0 FROM VideoInfos WHERE path IS NOT NULL";
            var rows = new List<Row>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new Row(r.GetInt32(0), r.GetString(1), r.GetInt64(2) == 1));
            return rows;
        }

        /// <summary>The path with the alias at the start replaced by the main name.</summary>
        private static string Rename(string path, PathSynonym s)
        {
            char sep = PathMapper.IsWindowsForm(s.Main) ? '\\' : '/';
            char other = sep == '\\' ? '/' : '\\';
            return s.Main + path.Substring(Math.Min(s.Alias.Length, path.Length)).TrimStart('\\', '/').Replace(other, sep);
        }

        private static bool Under(string path, string root) =>
            path.StartsWith(root, PathMapper.IsWindowsForm(root) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static StringComparer KeyComparer(PathSynonym s) =>
            PathMapper.IsWindowsForm(s.Main) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>
        /// Counts the videos stored under the alias, and checks a sample of them under the main name on this
        /// computer (through the saved Windows/Linux pairs when the main name is the other system's form).
        /// </summary>
        public static async Task<MappingCheck> CheckAsync(PathSynonym synonym, IProgress<MappingCheck> progress, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(synonym?.Main) || string.IsNullOrWhiteSpace(synonym?.Alias))
                return new MappingCheck(MappingCheckLevel.Info, "");
            var s = new PathSynonym { Main = PathMapper.NormalizeRoot(synonym.Main), Alias = PathMapper.NormalizeRoot(synonym.Alias) };
            if (PathMapper.SameRoot(s.Main, s.Alias))
                return new MappingCheck(MappingCheckLevel.Error, "The two names are the same.");
            if (PathMapper.IsWindowsForm(s.Main) != PathMapper.IsWindowsForm(s.Alias))
                return new MappingCheck(MappingCheckLevel.Error, "A synonym joins two names on the same kind of system. For a Windows and a Linux name, use a path pair above.");

            progress?.Report(new MappingCheck(MappingCheckLevel.Checking, "Checking the database..."));
            var rows = await Task.Run(ReadRows, ct);
            var under = rows.Where(r => Under(r.Path, s.Alias)).OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
            int alreadyMain = rows.Count(r => Under(r.Path, s.Main));

            if (under.Count == 0)
                return new MappingCheck(MappingCheckLevel.Info,
                    $"No videos in the database use {s.Alias}, so there is nothing to update." +
                    (alreadyMain > 0 ? $" {alreadyMain:N0} videos already use {s.Main}." : ""));

            var existing = new HashSet<string>(rows.Select(r => r.Path), KeyComparer(s));
            int merges = under.Count(r => existing.Contains(Rename(r.Path, s)));

            // Check a sample spread over the list under the main name, as this computer sees it
            int sampleSize = Math.Min(SampleSize, under.Count);
            var sample = Enumerable.Range(0, sampleSize).Select(i => under[(int)((long)i * under.Count / sampleSize)]).ToList();
            int found = 0, checkedCount = 0;
            foreach (var row in sample)
            {
                ct.ThrowIfCancellationRequested();
                string local = PathMapper.PreviewLocal(Rename(row.Path, s), PathMapper.Mappings);
                if (await Task.Run(() => File.Exists(local), ct)) found++;
                checkedCount++;
                progress?.Report(new MappingCheck(MappingCheckLevel.Checking, $"Checking files... {found} of {checkedCount} found under {s.Main}", checkedCount, found, sampleSize, under.Count));
            }

            string counts = $"{under.Count:N0} videos in the database use {s.Alias}. {found} of {sampleSize} were found under {s.Main} during the check.";
            string update = $" \"Update paths\" changes them to {s.Main}" + (merges > 0 ? $" and merges {Fmt.Plural(merges, "video that is", "videos that are")} stored under both names." : ".");
            if (found * 10 >= sampleSize * 8) return new MappingCheck(MappingCheckLevel.Ok, counts + update, checkedCount, found, sampleSize, under.Count);
            if (found > 0) return new MappingCheck(MappingCheckLevel.Warning, counts + " Check that both names point to the same folder." + update, checkedCount, found, sampleSize, under.Count);
            return new MappingCheck(MappingCheckLevel.Error, counts + " This synonym might be wrong, so the paths can't be updated.", checkedCount, found, sampleSize, under.Count);
        }

        /// <summary>
        /// Rewrites every stored path under the alias to the main name. When a video is stored under both names,
        /// the two rows become one: the one with screenshots is kept (else the one already under the main name).
        /// </summary>
        public static SynonymUpdateReport Update(PathSynonym synonym)
        {
            var s = new PathSynonym { Main = PathMapper.NormalizeRoot(synonym.Main), Alias = PathMapper.NormalizeRoot(synonym.Alias) };
            var rows = ReadRows();
            var byPath = new Dictionary<string, Row>(KeyComparer(s));
            foreach (var r in rows) byPath.TryAdd(r.Path, r);
            var removedIds = new List<int>();
            int updated = 0, merged = 0;

            using var db = new VideoInfoContext();
            var conn = db.Database.GetDbConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();
            void Exec(string sql, params (string, object)[] args)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                foreach (var (name, value) in args)
                {
                    var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p);
                }
                cmd.ExecuteNonQuery();
            }

            foreach (var row in rows.Where(r => Under(r.Path, s.Alias)))
            {
                string newPath = Rename(row.Path, s);
                if (byPath.TryGetValue(newPath, out var other) && other.Id != row.Id)
                {
                    // Stored under both names: keep one row
                    bool keepAlias = row.HasShots && !other.HasShots;
                    var drop = keepAlias ? other : row;
                    Exec("DELETE FROM VideoInfos WHERE id = $id", ("$id", drop.Id));
                    removedIds.Add(drop.Id);
                    if (keepAlias)
                    {
                        Exec("UPDATE VideoInfos SET path = $p WHERE id = $id", ("$p", newPath), ("$id", row.Id));
                        byPath[newPath] = row;
                    }
                    merged++;
                }
                else
                {
                    Exec("UPDATE VideoInfos SET path = $p WHERE id = $id", ("$p", newPath), ("$id", row.Id));
                    byPath[newPath] = row;
                    updated++;
                }
            }
            tx.Commit();

            // Screenshots of the rows that were merged away
            foreach (var id in removedIds)
                for (int i = 1; i <= 3; i++)
                {
                    try { File.Delete(Path.Combine(DataLocation.ScreenshotFolder, $"{id}_{i}.png")); } catch { }
                }

            string message = $"Updated {Fmt.Plural(updated, "path", "paths")} to {s.Main}" +
                             (merged > 0 ? $" and merged {Fmt.Plural(merged, "video", "videos")} stored under both names." : ".");
            return new SynonymUpdateReport(updated, merged, message);
        }
    }
}

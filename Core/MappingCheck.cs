using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace deepduplicates
{
    public enum MappingCheckLevel { Checking, Ok, Warning, Error, Info }

    /// <summary>The result (or progress) of checking one Windows/Linux path pair against the database.</summary>
    public sealed record MappingCheck(MappingCheckLevel Level, string Message, int Checked = 0, int Found = 0, int Sample = 0, int InDatabase = 0);

    /// <summary>
    /// Checks a path pair before it is saved. The database is the source of truth: its paths were read when
    /// the videos were scanned. The check takes a sample of the stored paths that start with the other
    /// computer's side of the pair, translates them with this pair only, and checks that the files exist here.
    /// So it tests the pair itself, not the paths in the database.
    /// </summary>
    public static class MappingChecker
    {
        public const int SampleSize = 10;

        public static async Task<MappingCheck> CheckAsync(PathMapping mapping, IProgress<MappingCheck> progress, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(mapping?.Windows) || string.IsNullOrWhiteSpace(mapping?.Linux))
                return new MappingCheck(MappingCheckLevel.Info, "");

            // A pair written with a synonym's alias (e.g. "L:\") works on the synonym's main name
            string win = PathMapper.NormalizeRoot(PathMapper.ToMainName(mapping.Windows.Trim().Replace('/', '\\').TrimEnd('\\') + "\\"));
            string lin = mapping.Linux.Trim().TrimEnd('/') + "/";
            bool onWindows = OperatingSystem.IsWindows();
            string localRoot = onWindows ? win : lin;   // where the pair should lead on this computer
            var pair = new[] { new PathMapping { Windows = win, Linux = lin } };

            progress?.Report(new MappingCheck(MappingCheckLevel.Checking, "Checking the database..."));

            // The stored paths as they are in the database (Windows form for mapped paths, or the form of
            // the computer that scanned them before a mapping existed)
            List<string> stored = await Task.Run(() =>
            {
                using var db = new VideoInfoContext();
                return db.Database.SqlQueryRaw<string>("SELECT path AS Value FROM VideoInfos WHERE path IS NOT NULL").ToList()
                    .Select(PathMapper.ToMainName).ToList();   // paths still stored under an alias count as the main name
            }, ct);

            // The source is the other computer's side of the pair, as stored in the database. Paths already
            // stored with this computer's side don't need the pair, so they say nothing about it.
            bool FromOtherSide(string p) => onWindows
                ? p.StartsWith(lin, StringComparison.Ordinal)
                : p.StartsWith(win, StringComparison.OrdinalIgnoreCase);
            bool AlreadyLocal(string p) => onWindows
                ? p.StartsWith(win, StringComparison.OrdinalIgnoreCase)
                : p.StartsWith(lin, StringComparison.Ordinal);

            var under = stored.Where(FromOtherSide).Select(p => PathMapper.PreviewLocal(p, pair)).ToList();
            under.Sort(StringComparer.OrdinalIgnoreCase);
            int alreadyLocal = stored.Count(AlreadyLocal);
            string otherRoot = onWindows ? lin : win;

            bool rootExists = await Task.Run(() => Directory.Exists(localRoot), ct);

            if (under.Count == 0)
            {
                if (alreadyLocal > 0)
                    return new MappingCheck(MappingCheckLevel.Info,
                        $"The {alreadyLocal:N0} videos under this path are stored with this computer's path ({localRoot}), so this pair isn't needed to find them here. It can't be checked until videos are scanned from {otherRoot}.");
                return rootExists
                    ? new MappingCheck(MappingCheckLevel.Info, $"No videos in the database start with {otherRoot} yet, so this pair can't be checked. The folder {localRoot} exists on this computer.")
                    : new MappingCheck(MappingCheckLevel.Error, $"The folder {localRoot} was not found on this computer, and no videos in the database start with {otherRoot}.");
            }

            // Spread the sample over the whole list, so it covers different folders
            int sampleSize = Math.Min(SampleSize, under.Count);
            var sample = Enumerable.Range(0, sampleSize).Select(i => under[(int)((long)i * under.Count / sampleSize)]).ToList();

            int found = 0, checkedCount = 0;
            foreach (var path in sample)
            {
                ct.ThrowIfCancellationRequested();
                if (await Task.Run(() => File.Exists(path), ct)) found++;
                checkedCount++;
                progress?.Report(new MappingCheck(MappingCheckLevel.Checking, $"Checking files... {found} of {checkedCount} found", checkedCount, found, sampleSize, under.Count));
            }

            string counts = $"{found} of {sampleSize} files found during the check ({under.Count:N0} videos in the database start with {otherRoot}).";
            if (found == sampleSize) return new MappingCheck(MappingCheckLevel.Ok, "This path looks valid: " + counts, checkedCount, found, sampleSize, under.Count);
            if (found * 10 >= sampleSize * 8)
                return new MappingCheck(MappingCheckLevel.Ok, "This path looks valid: " + counts + " The missing ones may have been deleted or moved.", checkedCount, found, sampleSize, under.Count);
            if (found > 0)
                return new MappingCheck(MappingCheckLevel.Warning, "Only " + counts + " Check that both sides point to the same folder.", checkedCount, found, sampleSize, under.Count);
            return new MappingCheck(MappingCheckLevel.Error,
                $"This path pair might be wrong: {counts}" + (rootExists ? "" : $" The folder {localRoot} was not found on this computer."),
                checkedCount, found, sampleSize, under.Count);
        }
    }
}

namespace deepduplicates
{
    /// <summary>Reports progress on the calling thread right away (unlike Progress&lt;T&gt;, which posts later).</summary>
    public sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> action;
        public SyncProgress(Action<T> action) => this.action = action;
        public void Report(T value) => action(value);
    }
}

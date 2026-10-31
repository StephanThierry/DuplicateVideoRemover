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
    /// Checks a path pair before it is saved: takes a sample of the videos in the database that sit under
    /// either side of the pair, translates each to this computer's side, and checks that the file is there.
    /// </summary>
    public static class MappingChecker
    {
        public const int SampleSize = 10;

        public static async Task<MappingCheck> CheckAsync(PathMapping mapping, IProgress<MappingCheck> progress, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(mapping?.Windows) || string.IsNullOrWhiteSpace(mapping?.Linux))
                return new MappingCheck(MappingCheckLevel.Info, "");

            string win = mapping.Windows.Trim().Replace('/', '\\').TrimEnd('\\') + "\\";
            string lin = mapping.Linux.Trim().TrimEnd('/') + "/";
            string localRoot = OperatingSystem.IsWindows() ? win : lin;
            var pair = new[] { new PathMapping { Windows = win, Linux = lin } };

            progress?.Report(new MappingCheck(MappingCheckLevel.Checking, "Checking the database..."));

            // The stored paths as they are in the database (Windows form for mapped paths, or the form of
            // the computer that scanned them before a mapping existed)
            List<string> stored = await Task.Run(() =>
            {
                using var db = new VideoInfoContext();
                return db.Database.SqlQueryRaw<string>("SELECT path AS Value FROM VideoInfos WHERE path IS NOT NULL").ToList();
            }, ct);

            // A video belongs to this pair when its stored path starts with either side, or when the saved
            // path pairs already turn it into a path under this computer's side (e.g. a pair for a sub folder)
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var under = new List<string>();
            foreach (var p in stored)
            {
                if (p.StartsWith(win, StringComparison.OrdinalIgnoreCase) || p.StartsWith(lin, StringComparison.Ordinal))
                    under.Add(PathMapper.PreviewLocal(p, pair));
                else
                {
                    string local = PathMapper.ToLocal(p);
                    if (local.StartsWith(localRoot, cmp)) under.Add(local);
                }
            }
            under.Sort(StringComparer.OrdinalIgnoreCase);

            bool rootExists = await Task.Run(() => Directory.Exists(localRoot), ct);

            if (under.Count == 0)
            {
                return rootExists
                    ? new MappingCheck(MappingCheckLevel.Info, $"No videos in the database use this path yet, so it can't be checked. The folder {localRoot} exists on this computer.")
                    : new MappingCheck(MappingCheckLevel.Error, $"The folder {localRoot} was not found on this computer, and no videos in the database use this path.");
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

            string counts = $"{found} of {sampleSize} files found during the check ({under.Count:N0} videos in the database use this path).";
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

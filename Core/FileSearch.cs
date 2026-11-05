using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace deepduplicates
{
    /// <summary>
    /// A search like  vacation lisbon 2026 -ocean : every word must be in the path, a word starting with "-" must
    /// not be. Not case sensitive. "Quoted words" are searched as one phrase, also after "-".
    /// </summary>
    public sealed record SearchQuery(string[] Include, string[] Exclude)
    {
        public bool IsEmpty => Include.Length == 0 && Exclude.Length == 0;

        public static SearchQuery Parse(string text)
        {
            var include = new List<string>();
            var exclude = new List<string>();
            text ??= "";
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) break;
                bool not = text[i] == '-';
                if (not) i++;
                string term;
                if (i < text.Length && text[i] == '"')
                {
                    int end = text.IndexOf('"', i + 1);
                    if (end < 0) end = text.Length;
                    term = text.Substring(i + 1, end - i - 1);
                    i = end + 1;
                }
                else
                {
                    int start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                    term = text.Substring(start, i - start);
                }
                term = term.Trim().ToLowerInvariant();
                if (term.Length == 0) continue;     // a lone "-" or ""
                (not ? exclude : include).Add(term);
            }
            return new SearchQuery(include.Distinct().ToArray(), exclude.Distinct().ToArray());
        }
    }

    /// <summary>How the All files list is ordered.</summary>
    public enum FileSort { Path, NameUp, NameDown, FolderUp, FolderDown, SizeUp, SizeDown }

    public static class FileSorting
    {
        /// <summary>(label, value) for a sort drop-down, in display order.</summary>
        public static readonly (string Label, FileSort Sort)[] Choices =
        {
            ("Path (default)", FileSort.Path),
            ("Filename, A to Z", FileSort.NameUp), ("Filename, Z to A", FileSort.NameDown),
            ("Folder name, A to Z", FileSort.FolderUp), ("Folder name, Z to A", FileSort.FolderDown),
            ("Size, smallest first", FileSort.SizeUp), ("Size, largest first", FileSort.SizeDown),
        };

        /// <summary>The list in the chosen order (ties by path, so the order is the same every time). The list is kept as it is for Path.</summary>
        public static List<VideoEntry> Apply(List<VideoEntry> list, FileSort sort)
        {
            var cmp = StringComparer.OrdinalIgnoreCase;
            IOrderedEnumerable<VideoEntry> ordered = sort switch
            {
                FileSort.NameUp => list.OrderBy(e => e.FileName, cmp).ThenBy(e => e.Folder, cmp),
                FileSort.NameDown => list.OrderByDescending(e => e.FileName, cmp).ThenBy(e => e.Folder, cmp),
                FileSort.FolderUp => list.OrderBy(e => e.Folder, cmp).ThenBy(e => e.FileName, cmp),
                FileSort.FolderDown => list.OrderByDescending(e => e.Folder, cmp).ThenBy(e => e.FileName, cmp),
                FileSort.SizeUp => list.OrderBy(e => e.SizeBytes).ThenBy(e => e.Path, cmp),
                FileSort.SizeDown => list.OrderByDescending(e => e.SizeBytes).ThenBy(e => e.Path, cmp),
                _ => null
            };
            return ordered == null ? list : ordered.ToList();
        }
    }

    /// <summary>
    /// All videos the last scan found, ready for searching: the paths are lower-cased once, so a search over
    /// 60,000+ files is only substring checks (a few milliseconds).
    /// </summary>
    public sealed class FileIndex
    {
        private readonly VideoEntry[] entries;
        private readonly string[] lowerPaths;

        public int Count => entries.Length;
        public IReadOnlyList<VideoEntry> All => entries;

        public FileIndex(IEnumerable<VideoEntry> videos)
        {
            entries = videos.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            lowerPaths = entries.Select(e => e.Path.ToLowerInvariant()).ToArray();
        }

        /// <summary>The videos whose path contains every include term and none of the exclude terms.</summary>
        public List<VideoEntry> Search(SearchQuery query, Func<VideoEntry, bool> alsoKeep = null)
        {
            var found = new List<VideoEntry>();
            var include = query.Include;
            var exclude = query.Exclude;
            for (int i = 0; i < entries.Length; i++)
            {
                string path = lowerPaths[i];
                bool ok = true;
                for (int t = 0; t < include.Length && ok; t++) ok = path.Contains(include[t], StringComparison.Ordinal);
                for (int t = 0; t < exclude.Length && ok; t++) ok = !path.Contains(exclude[t], StringComparison.Ordinal);
                if (ok && (alsoKeep == null || alsoKeep(entries[i]))) found.Add(entries[i]);
            }
            return found;
        }

        public List<VideoEntry> Search(string text, Func<VideoEntry, bool> alsoKeep = null) => Search(SearchQuery.Parse(text), alsoKeep);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace deepduplicates
{
    /// <summary>
    /// A search like  vacation lisbon 2026 -ocean : every word must be in the path, a word starting with "-" must
    /// not be. Not case sensitive. "Quoted words" are searched as one phrase, in that order, also after "-".
    /// Inside quotes a regular expression works: "night.?drive" finds nightdrive and night3drive. The quoted text
    /// is also tried as plain text, so "file (1)" still finds exactly that.
    /// </summary>
    public sealed record SearchQuery(string[] Include, string[] Exclude, Regex[] IncludeRx = null, Regex[] ExcludeRx = null)
    {
        private static readonly Regex[] None = Array.Empty<Regex>();
        public Regex[] IncludeRx { get; init; } = IncludeRx ?? None;
        public Regex[] ExcludeRx { get; init; } = ExcludeRx ?? None;

        public bool IsEmpty => Include.Length == 0 && Exclude.Length == 0 && IncludeRx.Length == 0 && ExcludeRx.Length == 0;

        /// <summary>Same for equal searches (to see whether the text was typed on meanwhile).</summary>
        public string Key => string.Join("\u0001", Include) + "\u0002" + string.Join("\u0001", Exclude) + "\u0002"
            + string.Join("\u0001", IncludeRx.Select(r => r.ToString())) + "\u0002" + string.Join("\u0001", ExcludeRx.Select(r => r.ToString()));

        /// <summary>True when the (lower-cased or not) path fits the search.</summary>
        public bool Matches(string path)
        {
            if (IsEmpty) return true;
            foreach (var t in Include) if (path.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) return false;
            foreach (var t in Exclude) if (path.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return MatchesRegex(path);
        }

        /// <summary>Only the regular-expression terms (the plain terms are checked by the caller).</summary>
        public bool MatchesRegex(string path)
        {
            foreach (var r in IncludeRx) if (!Safe(r, path)) return false;
            foreach (var r in ExcludeRx) if (Safe(r, path)) return false;
            return true;
        }

        private static bool Safe(Regex r, string path)
        {
            try { return r.IsMatch(path); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        private static bool HasRegexChars(string s) => s.IndexOfAny(new[] { '\\', '.', '?', '*', '+', '[', ']', '(', ')', '{', '}', '|', '^', '$' }) >= 0;

        /// <summary>The term as a regex that finds the pattern or the same text literally; plain text when it has no regex characters.</summary>
        private static Regex ToRegex(string raw)
        {
            string literal = Regex.Escape(raw);
            string pattern = literal;
            try
            {
                new Regex(raw, RegexOptions.CultureInvariant);   // only to see that it is valid
                pattern = $"(?:{literal})|(?:{raw})";
            }
            catch (ArgumentException) { }                         // not a valid regex: plain text only
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }

        public static SearchQuery Parse(string text)
        {
            var include = new List<string>();
            var exclude = new List<string>();
            var includeRx = new List<Regex>();
            var excludeRx = new List<Regex>();
            // Typographic quotes (from a phone keyboard or a pasted text) count as plain quotes
            text = (text ?? "").Replace('\u201C', '"').Replace('\u201D', '"');
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) break;
                bool not = text[i] == '-';
                if (not) i++;
                string term;
                bool quoted = false;
                if (i < text.Length && text[i] == '"')
                {
                    quoted = true;
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
                if (quoted ? term.Trim().Length == 0 : term.Length == 0) continue;     // a lone "-" or ""
                if (quoted && HasRegexChars(term)) (not ? excludeRx : includeRx).Add(ToRegex(term.Trim()));
                else (not ? exclude : include).Add(term.Trim().ToLowerInvariant());
            }
            return new SearchQuery(include.Distinct().ToArray(), exclude.Distinct().ToArray(), includeRx.ToArray(), excludeRx.ToArray());
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
                if (ok && (query.IncludeRx.Length > 0 || query.ExcludeRx.Length > 0)) ok = query.MatchesRegex(path);
                if (ok && (alsoKeep == null || alsoKeep(entries[i]))) found.Add(entries[i]);
            }
            return found;
        }

        public List<VideoEntry> Search(string text, Func<VideoEntry, bool> alsoKeep = null) => Search(SearchQuery.Parse(text), alsoKeep);
    }
}

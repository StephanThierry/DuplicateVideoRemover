using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace deepduplicates
{
    /// <summary>
    /// Translates paths so the same database (and settings.json) can be used from several computers.
    ///
    /// Two kinds of rules, read from pathmapping.json in the data folder at launch:
    ///
    /// Path pairs (mappings): the same storage on Windows and on Linux.
    ///   { "windows": "\\\\nas\\video\\", "linux": "/videos/" }
    ///   Paths are stored in the database in Windows form and converted to Linux form on Linux.
    ///
    /// Synonyms: other names for the same storage on the same kind of system, e.g. a mapped drive letter
    /// and the network (UNC) path it points to.
    ///   { "main": "\\\\nas\\video\\", "alias": "L:\\" }
    ///   The main name is what goes into the database. A path under an alias is read and stored as the main
    ///   name. On a computer where the main name can't be reached but the alias can, the alias is used there.
    ///
    /// Paths that match no rule are left unchanged.
    /// </summary>
    public static class PathMapper
    {
        public const string FileName = "pathmapping.json";

        private static List<PathMapping> mappings = new List<PathMapping>();
        private static List<PathSynonym> synonyms = new List<PathSynonym>();
        // Which root to use on this computer for each synonym (checked once, network folders can be slow)
        private static readonly ConcurrentDictionary<PathSynonym, string> localRootCache = new();

        public static bool IsWindows => OperatingSystem.IsWindows();

        public static IReadOnlyList<PathMapping> Mappings => mappings;
        public static IReadOnlyList<PathSynonym> Synonyms => synonyms;

        public static void Load(string appFolder)
        {
            string path = Path.Combine(appFolder, FileName);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, TemplateJson);
                Console.WriteLine($"Created '{FileName}' (no mappings yet). Add mappings to share the database between Windows and Linux.");
                mappings = new List<PathMapping>();
                synonyms = new List<PathSynonym>();
                localRootCache.Clear();
                return;
            }

            PathMappingFile file;
            try
            {
                file = JsonSerializer.Deserialize<PathMappingFile>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException e)
            {
                throw new InvalidDataException($"'{path}' is not valid JSON: {e.Message}");
            }

            synonyms = NormalizeSynonyms(file?.Synonyms ?? new List<PathSynonym>());
            mappings = Normalize(file?.Mappings ?? new List<PathMapping>(), synonyms);
            localRootCache.Clear();

            foreach (var s in synonyms) Console.WriteLine($"Path synonym: {s.Alias}  =  {s.Main}");
            foreach (var m in mappings) Console.WriteLine($"Path mapping: {m.Windows}  <->  {m.Linux}");
        }

        /// <summary>Replaces the path pairs and saves pathmapping.json (keeps the synonyms).</summary>
        public static void Save(string appFolder, IEnumerable<PathMapping> newMappings) =>
            Save(appFolder, newMappings, synonyms);

        /// <summary>Replaces the path pairs and synonyms and saves pathmapping.json (used by the interfaces).</summary>
        public static void Save(string appFolder, IEnumerable<PathMapping> newMappings, IEnumerable<PathSynonym> newSynonyms)
        {
            var clean = newMappings
                .Where(m => !string.IsNullOrWhiteSpace(m.Windows) && !string.IsNullOrWhiteSpace(m.Linux))
                .Select(m => new PathMapping { Windows = m.Windows.Trim(), Linux = m.Linux.Trim() })
                .ToList();
            var cleanSynonyms = newSynonyms
                .Where(s => !string.IsNullOrWhiteSpace(s.Main) && !string.IsNullOrWhiteSpace(s.Alias))
                .Select(s => new PathSynonym { Main = NormalizeRoot(s.Main), Alias = NormalizeRoot(s.Alias) })
                .ToList();
            var json = JsonSerializer.Serialize(new PathMappingFile { Mappings = clean, Synonyms = cleanSynonyms },
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.WriteAllText(Path.Combine(appFolder, FileName), json);
            Load(appFolder);
        }

        private static List<PathMapping> Normalize(IEnumerable<PathMapping> raw, List<PathSynonym> syns) => raw
            .Where(m => !string.IsNullOrWhiteSpace(m.Windows) && !string.IsNullOrWhiteSpace(m.Linux))
            // A pair written with an alias ("L:\") works on the main name, which is what the database holds
            .Select(m => new PathMapping { Windows = ApplySynonyms(NormalizeWindowsRoot(m.Windows), syns), Linux = NormalizeLinuxRoot(m.Linux) })
            // Longest prefix first, so "L:\Video\" wins over "L:\"
            .OrderByDescending(m => Math.Max(m.Windows.Length, m.Linux.Length))
            .ToList();

        public static List<PathSynonym> NormalizeSynonyms(IEnumerable<PathSynonym> raw) => raw
            .Where(s => !string.IsNullOrWhiteSpace(s.Main) && !string.IsNullOrWhiteSpace(s.Alias))
            .Select(s => new PathSynonym { Main = NormalizeRoot(s.Main), Alias = NormalizeRoot(s.Alias) })
            .Where(s => !SameRoot(s.Main, s.Alias))
            .OrderByDescending(s => s.Alias.Length)
            .ToList();

        // --- Converting ---

        /// <summary>Converts a path (in any known form) to the form used on the current OS.</summary>
        public static string ToLocal(string path) => ToLocal(path, mappings, synonyms, useLocalAlias: true);

        /// <summary>What ToLocal would return with these (unsaved) pairs - for "try a path" in the interfaces.</summary>
        public static string PreviewLocal(string path, IEnumerable<PathMapping> unsaved) =>
            ToLocal(path, Normalize(unsaved, synonyms), synonyms, useLocalAlias: false);

        /// <summary>Converts a path to the form stored in the database: synonyms to their main name, then Windows form.</summary>
        public static string ToStorage(string path) => ToWindows(ApplySynonyms(path, synonyms), mappings);

        /// <summary>Replaces an alias at the start of the path by the synonym's main name.</summary>
        public static string ToMainName(string path) => ApplySynonyms(path, synonyms);

        public static string ToWindows(string path) => ToWindows(ApplySynonyms(path, synonyms), mappings);

        public static string ToLinux(string path) => ToLinux(ApplySynonyms(path, synonyms), mappings);

        private static string ToLocal(string path, List<PathMapping> pairs, List<PathSynonym> syns, bool useLocalAlias)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string p = ApplySynonyms(path, syns);
            p = IsWindows ? ToWindows(p, pairs) : ToLinux(p, pairs);
            if (useLocalAlias)
            {
                // Where the main name can't be reached but the alias can, use the alias on this computer
                foreach (var s in syns)
                {
                    if (!StartsWithRoot(p, s.Main, Comparison(s.Main))) continue;
                    string root = LocalRoot(s);
                    if (root != s.Main) return root + p.Substring(TrimmedLength(s.Main, p)).TrimStart('\\', '/').Replace(Other(root), Separator(root));
                    break;
                }
            }
            return p;
        }

        private static string ApplySynonyms(string path, List<PathSynonym> syns)
        {
            if (string.IsNullOrEmpty(path)) return path;
            foreach (var s in syns)
            {
                if (StartsWithRoot(path, s.Alias, Comparison(s.Alias)))
                    return s.Main + path.Substring(TrimmedLength(s.Alias, path)).TrimStart('\\', '/').Replace(Other(s.Main), Separator(s.Main));
            }
            return path;
        }

        /// <summary>The main name if this computer can reach it, else the alias if that works, else the main name.</summary>
        private static string LocalRoot(PathSynonym s) => localRootCache.GetOrAdd(s, syn =>
        {
            bool mainIsLocalForm = IsWindowsForm(syn.Main) == IsWindows;
            if (!mainIsLocalForm) return syn.Main;
            try
            {
                if (Directory.Exists(syn.Main)) return syn.Main;
                if (IsWindowsForm(syn.Alias) == IsWindows && Directory.Exists(syn.Alias)) return syn.Alias;
            }
            catch { }
            return syn.Main;
        });

        private static string ToWindows(string path, List<PathMapping> list)
        {
            if (string.IsNullOrEmpty(path)) return path;
            foreach (var m in list)
            {
                if (StartsWithRoot(path, m.Linux, StringComparison.Ordinal))
                    return m.Windows + path.Substring(TrimmedLength(m.Linux, path)).TrimStart('/').Replace('/', '\\');
            }
            return path;
        }

        private static string ToLinux(string path, List<PathMapping> list)
        {
            if (string.IsNullOrEmpty(path)) return path;
            foreach (var m in list)
            {
                if (StartsWithRoot(path, m.Windows, StringComparison.OrdinalIgnoreCase))
                    return m.Linux + path.Substring(TrimmedLength(m.Windows, path)).TrimStart('\\', '/').Replace('\\', '/');
            }
            return path;
        }

        // --- Helpers ---

        /// <summary>Drive letter ("L:") or network path ("\\server") = Windows form; otherwise Linux form.</summary>
        public static bool IsWindowsForm(string path) =>
            path.StartsWith(@"\\") || (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]));

        private static StringComparison Comparison(string root) =>
            IsWindowsForm(root) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static char Separator(string root) => IsWindowsForm(root) ? '\\' : '/';
        private static char Other(string root) => IsWindowsForm(root) ? '/' : '\\';

        /// <summary>A root with a trailing separator in the style of its own form.</summary>
        public static string NormalizeRoot(string root) =>
            IsWindowsForm(root.Trim()) ? NormalizeWindowsRoot(root) : NormalizeLinuxRoot(root);

        public static bool SameRoot(string a, string b) =>
            string.Equals(NormalizeRoot(a), NormalizeRoot(b), IsWindowsForm(a) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        // A root always ends with a separator ("L:\", "/mnt/architecture/"), but a path may be
        // the root itself without the trailing separator ("L:", "/mnt/architecture").
        private static bool StartsWithRoot(string path, string root, StringComparison comparison)
        {
            return path.StartsWith(root, comparison)
                || string.Equals(path, root.Substring(0, root.Length - 1), comparison);
        }

        private static int TrimmedLength(string root, string path) => Math.Min(root.Length, path.Length);

        private static string NormalizeWindowsRoot(string root)
        {
            root = root.Trim().Replace('/', '\\');
            return root.EndsWith("\\") ? root : root + "\\";
        }

        private static string NormalizeLinuxRoot(string root)
        {
            root = root.Trim();
            return root.EndsWith("/") ? root : root + "/";
        }

        private const string TemplateJson =
@"{
  ""mappings"": [
  ],
  ""synonyms"": [
  ],
  ""_example"": {
    ""mappings"": [ { ""windows"": ""\\\\nas.protek.dk\\video\\"", ""linux"": ""/mnt/video/"" } ],
    ""synonyms"": [ { ""main"": ""\\\\nas.protek.dk\\video\\"", ""alias"": ""L:\\"" } ]
  }
}
";
    }

    public class PathMappingFile
    {
        public List<PathMapping> Mappings { get; set; }
        public List<PathSynonym> Synonyms { get; set; }
    }

    public class PathMapping
    {
        public string Windows { get; set; }
        public string Linux { get; set; }
    }

    /// <summary>Two names for the same storage on the same kind of system. Main is stored in the database.</summary>
    public class PathSynonym
    {
        public string Main { get; set; }
        public string Alias { get; set; }
    }
}

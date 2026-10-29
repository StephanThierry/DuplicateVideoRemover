using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace deepduplicates
{
    /// <summary>
    /// Translates paths between Windows and Linux, so the same database (and settings.json)
    /// can be used from both operating systems.
    ///
    /// Mappings are read from pathmapping.json in the app folder at launch, e.g.
    ///   { "mappings": [ { "windows": "L:\\", "linux": "/mnt/architecture/" } ] }
    ///
    /// Paths are stored in the database in Windows form (as existing databases already are),
    /// and converted to the form of the current OS when read. Paths that match no mapping
    /// are left unchanged.
    /// </summary>
    public static class PathMapper
    {
        public const string FileName = "pathmapping.json";

        private static List<PathMapping> mappings = new List<PathMapping>();

        public static bool IsWindows => OperatingSystem.IsWindows();

        public static IReadOnlyList<PathMapping> Mappings => mappings;

        public static void Load(string appFolder)
        {
            string path = Path.Combine(appFolder, FileName);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, TemplateJson);
                Console.WriteLine($"Created '{FileName}' (no mappings yet). Add mappings to share the database between Windows and Linux.");
                mappings = new List<PathMapping>();
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

            mappings = Normalize(file?.Mappings ?? new List<PathMapping>());

            foreach (var m in mappings) Console.WriteLine($"Path mapping: {m.Windows}  <->  {m.Linux}");
        }

        /// <summary>Replaces the mappings and saves pathmapping.json (used by the interfaces).</summary>
        public static void Save(string appFolder, IEnumerable<PathMapping> newMappings)
        {
            var clean = newMappings
                .Where(m => !string.IsNullOrWhiteSpace(m.Windows) && !string.IsNullOrWhiteSpace(m.Linux))
                .Select(m => new PathMapping { Windows = m.Windows.Trim(), Linux = m.Linux.Trim() })
                .ToList();
            var json = JsonSerializer.Serialize(new PathMappingFile { Mappings = clean },
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.WriteAllText(Path.Combine(appFolder, FileName), json);
            Load(appFolder);
        }

        private static List<PathMapping> Normalize(IEnumerable<PathMapping> raw) => raw
            .Where(m => !string.IsNullOrWhiteSpace(m.Windows) && !string.IsNullOrWhiteSpace(m.Linux))
            .Select(m => new PathMapping { Windows = NormalizeWindowsRoot(m.Windows), Linux = NormalizeLinuxRoot(m.Linux) })
            // Longest prefix first, so "L:\Video\" wins over "L:\"
            .OrderByDescending(m => Math.Max(m.Windows.Length, m.Linux.Length))
            .ToList();

        /// <summary>Converts a path (in either form) to the form used on the current OS.</summary>
        public static string ToLocal(string path) => IsWindows ? ToWindows(path) : ToLinux(path);

        /// <summary>What ToLocal would return with these (unsaved) mappings - for "try a path" in the interfaces.</summary>
        public static string PreviewLocal(string path, IEnumerable<PathMapping> unsaved)
        {
            var list = Normalize(unsaved);
            return IsWindows ? ToWindows(path, list) : ToLinux(path, list);
        }

        /// <summary>Converts a path to the form stored in the database (Windows form when a mapping matches).</summary>
        public static string ToStorage(string path) => ToWindows(path);

        public static string ToWindows(string path) => ToWindows(path, mappings);

        public static string ToLinux(string path) => ToLinux(path, mappings);

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
  ""_example"": [
    { ""windows"": ""L:\\"", ""linux"": ""/mnt/architecture/"" },
    { ""windows"": ""\\\\nas.protek.dk\\video\\"", ""linux"": ""/mnt/video/"" }
  ]
}
";
    }

    public class PathMappingFile
    {
        public List<PathMapping> Mappings { get; set; }
    }

    public class PathMapping
    {
        public string Windows { get; set; }
        public string Linux { get; set; }
    }
}

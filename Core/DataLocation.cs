using System;
using System.IO;

namespace deepduplicates
{
    /// <summary>
    /// Where things are stored.
    ///
    /// Data folder: settings.json, pathmapping.json and output (report, scripts, results).
    /// Database folder: deepdupes.db, the _screens screenshots and runhistory.json. The screenshots are named
    /// by database id, so they always live next to the database. By default the database folder is the data
    /// folder; it can be a separate folder so several installations (for example a Docker container and the
    /// desktop app on a PC) can each have their own settings but share one database.
    ///
    /// Each app reads --data / --db, then the environment variables DVR_DATA / DVR_DB.
    /// </summary>
    public static class DataLocation
    {
        public const string DatabaseFileName = "deepdupes.db";

        private static string databaseFolder;

        /// <summary>The folder with the database and screenshots (defaults to the data folder).</summary>
        public static string DatabaseFolder => databaseFolder ?? Directory.GetCurrentDirectory();

        public static string DatabasePath => Path.Combine(DatabaseFolder, DatabaseFileName);
        public static string ScreenshotFolder => Path.Combine(DatabaseFolder, "_screens");

        public static void SetDatabaseFolder(string folder)
        {
            folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(folder);
            databaseFolder = folder;
        }

        /// <summary>Value after a command-line option, e.g. Arg(args, "--data").</summary>
        public static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        /// <summary>--data, then DVR_DATA, then the fallback.</summary>
        public static string ResolveDataFolder(string[] args, string fallback) =>
            NotEmpty(Arg(args, "--data")) ?? NotEmpty(Environment.GetEnvironmentVariable("DVR_DATA")) ?? fallback;

        /// <summary>--db, then DVR_DB, then null (meaning: the data folder).</summary>
        public static string ResolveDatabaseFolder(string[] args) =>
            NotEmpty(Arg(args, "--db")) ?? NotEmpty(Environment.GetEnvironmentVariable("DVR_DB"));

        private static string NotEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}

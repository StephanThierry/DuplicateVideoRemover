using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
// Command-line version. Run from the folder that holds settings.json (the data folder).
// Publish a single executable:
// dotnet publish apps/Cli -r win-x64 -c Release /p:PublishSingleFile=true   (use -r linux-x64 for Linux)

namespace deepduplicates
{
    class Program
    {
        // Accuracy tested to 99.99% with default settings - so check the report for false positives. On average there is aprox. 1 i every 10000 files.
        // Usage: duplicatevideoremover [clean|index] [--data <folder>] [--db <folder>]
        //   --data  folder with settings.json and output (default: DVR_DATA, else the current folder)
        //   --db    folder with deepdupes.db and _screens (default: DVR_DB, else the data folder)
        static async Task<int> Main(string[] args)
        {
            string mode = args.Select(a => a.ToLowerInvariant()).FirstOrDefault(a => a == "clean" || a == "index");

            string dataFolder = System.IO.Path.GetFullPath(DataLocation.ResolveDataFolder(args, System.IO.Directory.GetCurrentDirectory()));
            System.IO.Directory.CreateDirectory(dataFolder);
            System.IO.Directory.SetCurrentDirectory(dataFolder);
            DataLocation.SetDatabaseFolder(DataLocation.ResolveDatabaseFolder(args) ?? dataFolder);
            Console.WriteLine($"Data folder: {dataFolder}");
            Console.WriteLine($"Database:    {DataLocation.DatabasePath}");

            // Load Windows <-> Linux path mappings first, the database uses them when reading/writing paths
            PathMapper.Load(dataFolder);

            // Only one scan per database at a time (the web or desktop app may use the same database)
            ScanLock scanLock;
            try { scanLock = ScanLock.Acquire("the command-line version"); }
            catch (ScanAlreadyRunningException e) { Console.WriteLine(e.Message); return 1; }
            using var _ = scanLock;

            VideoInfoContext.Prepare();
            VideoInfoContext db = new VideoInfoContext();

            FileHandler fileHandler = await FileHandler.CreateInstance();
            if (fileHandler.firstRun) return 0;
            ImageHandler imageHandler = new ImageHandler();

            VideoHandler videoHandler = new VideoHandler(fileHandler, imageHandler);
            List<VideoInfo> mediaList = await videoHandler.saveVideoMetadataAndScreenshots(db);

            if (mode == "clean") {
                Console.WriteLine("Clean-mode enabled - DB entries and images are truncated based on current filesystem!");
                CleanupHandler ch = new CleanupHandler(db, fileHandler);
                await ch.runCleanup();
            }

            if (mode == "index") {
                Console.WriteLine("Indexing enabled - complete index of all files is created!");
                fileHandler.generateIndexReport(mediaList);
            } 

            Console.WriteLine("Comparing videos...");
            var (results, analyzed) = Analyzer.Analyze(mediaList, fileHandler.settings);
            mediaList = analyzed;
            results.Save(); // the web and desktop interfaces can open these results

            Console.WriteLine("Generating delete script...");
            fileHandler.generateBatchFile(mediaList);

            Console.WriteLine("Generating HTML report...");
            fileHandler.generateReport(mediaList);

            Console.WriteLine("Generating encoding script...");
            fileHandler.generateEncoding(mediaList, fileHandler.settings.encodeSettings);

            Console.WriteLine("Check ./output directory");
            Console.WriteLine("Done!");
            return 0;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace deepduplicates
{
    /// <summary>What the current match settings would find in videos that are already read.</summary>
    public sealed record PreviewResult(int VideosInScope, int Duplicates, int Groups, int Broken, long DuplicateBytes);

    /// <summary>
    /// Everything the web and desktop interfaces share: the data folder, settings and path mappings,
    /// scanning, the live preview and the review session.
    /// </summary>
    public sealed class AppController
    {
        private List<VideoInfo> cachedMedia;
        private readonly object cacheLock = new();

        public string DataFolder { get; }
        public Settings Settings { get; private set; }
        /// <summary>True when settings.json did not exist and was created with defaults.</summary>
        public bool IsFirstRun { get; }
        public ScanEngine Scanner { get; } = new();
        public ResultSession Session { get; private set; }

        /// <summary>Raised when there is a new review session (after a scan, or new results).</summary>
        public event Action SessionReplaced;

        /// <summary>Raised when videos in the database changed (scan finished, files removed) - previews are outdated.</summary>
        public event Action DataChanged;

        private AppController(string dataFolder)
        {
            DataFolder = dataFolder;
            Settings = FileHandler.LoadSettings(out bool created);
            IsFirstRun = created;
            Session = new ResultSession(ResultSet.LoadSaved());
            Scanner.Completed += results =>
            {
                InvalidateCache();
                ReplaceSession(results);
                DataChanged?.Invoke();
            };
        }

        /// <summary>
        /// Opens a data folder (settings.json, database, screenshots). The engine works relative to the
        /// current directory, so this changes it. Call once at start-up.
        /// </summary>
        /// <param name="dataFolder">Settings and output</param>
        /// <param name="databaseFolder">Database and screenshots, when not in the data folder</param>
        public static AppController Open(string dataFolder, string databaseFolder = null)
        {
            dataFolder = Path.GetFullPath(dataFolder);
            Directory.CreateDirectory(dataFolder);
            Directory.SetCurrentDirectory(dataFolder);
            DataLocation.SetDatabaseFolder(databaseFolder ?? dataFolder);
            PathMapper.Load(dataFolder);
            VideoInfoContext.Prepare();
            return new AppController(dataFolder);
        }

        public string DatabaseFolder => DataLocation.DatabaseFolder;

        public FileHandler CreateFileHandler() => new FileHandler(Settings);

        public string ScreenshotPath(int videoId, int index) =>
            Path.Combine(DataLocation.ScreenshotFolder, videoId + "_" + index + ".png");

        public void SaveSettings(Settings settings, IEnumerable<PathMapping> mappings) =>
            SaveSettings(settings, mappings, PathMapper.Synonyms);

        public void SaveSettings(Settings settings, IEnumerable<PathMapping> mappings, IEnumerable<PathSynonym> synonyms)
        {
            settings.Normalize();
            PathMapper.Save(DataFolder, mappings, synonyms);
            FileHandler.SaveSettings(settings);
            Settings = settings.Clone();
        }

        /// <summary>Starts a scan with the saved settings.</summary>
        public bool StartScan() => Scanner.Start(Settings);

        /// <summary>Runs the match rules on videos already in the database - no files are read.</summary>
        public PreviewResult Preview(Settings settings)
        {
            var media = ScanEngine.InScope(CachedMedia(), settings);
            var (results, _) = Analyzer.Analyze(media, settings);
            return new PreviewResult(media.Count, results.DuplicateCount, results.Groups.Count, results.Broken.Count, results.DuplicateBytes);
        }

        /// <summary>Compares again with the saved settings, using what is in the database (no rescan).</summary>
        public void ReanalyzeFromDatabase()
        {
            var media = ScanEngine.InScope(CachedMedia(), Settings);
            var (results, _) = Analyzer.Analyze(media, Settings);
            results.Save();
            ReplaceSession(results);
        }

        /// <summary>Deletes the selected files.</summary>
        public ExecuteReport Execute()
        {
            var report = Session.Execute(CreateFileHandler());
            InvalidateCache();
            DataChanged?.Invoke();
            return report;
        }

        /// <summary>
        /// Videos in the scope that could be re-encoded (not duplicates or broken), highest average bitrate first.
        /// Uses the videos already read - no files are read.
        /// </summary>
        public List<EncodeCandidate> EncodeCandidates()
        {
            var media = ScanEngine.InScope(CachedMedia(), Settings);
            var (_, analyzed) = Analyzer.Analyze(media, Settings);
            return EncodePlanner.Candidates(analyzed, Settings.EncodeOrDefault());
        }

        /// <summary>Writes output/encode_selected.ps1 (Windows) or .sh (Linux) for these videos. Returns its path.</summary>
        public string WriteEncodeScript(IEnumerable<EncodeCandidate> videos) =>
            EncodePlanner.WriteScript(CreateFileHandler().outputFolder, EncodeScriptName, videos, Settings.EncodeOrDefault());

        public const string EncodeScriptName = "encode_selected";

        /// <summary>
        /// Rewrites the stored paths under the synonym's alias to its main name (see SynonymTools.Update).
        /// Not while a scan is running. Compares again afterwards, so the review uses the new paths.
        /// </summary>
        public SynonymUpdateReport UpdateSynonymPaths(PathSynonym synonym)
        {
            ScanLock scanLock;
            try { scanLock = ScanLock.Acquire("updating paths"); }
            catch (ScanAlreadyRunningException e) { return new SynonymUpdateReport(0, 0, e.Message); }
            using (scanLock)
            {
                var report = SynonymTools.Update(synonym);
                InvalidateCache();
                ReanalyzeFromDatabase();
                DataChanged?.Invoke();
                return report;
            }
        }

        /// <summary>When the last scan of this scope ran, if any.</summary>
        public RunRecord LastRun() => RunHistory.LastRun(Settings);

        private List<VideoInfo> CachedMedia()
        {
            lock (cacheLock) return cachedMedia ??= ScanEngine.LoadAllCached();
        }

        private void InvalidateCache()
        {
            lock (cacheLock) cachedMedia = null;
        }

        private void ReplaceSession(ResultSet results)
        {
            Session = new ResultSession(results);
            SessionReplaced?.Invoke();
        }
    }
}

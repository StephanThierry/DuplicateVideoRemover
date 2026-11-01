using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace deepduplicates
{
    /// <summary>A false positive with the two videos as they are in the database now (null when gone).</summary>
    public sealed record FalsePositiveDetail(FalsePositive Pair, VideoEntry NowA, VideoEntry NowB)
    {
        public string PathA => NowA?.Path ?? PathMapper.ToLocal(Pair.PathA);
        public string PathB => NowB?.Path ?? PathMapper.ToLocal(Pair.PathB);
    }

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

        /// <summary>Compares again with the saved settings, using what is in the database (no rescan).</summary>
        public void ReanalyzeFromDatabase()
        {
            var media = ScanEngine.InScope(CachedMedia(), Settings);
            var (results, _) = Analyzer.Analyze(media, Settings);
            results.Save();
            ReplaceSession(results);
        }

        /// <summary>Deletes the selected files, reporting each one as it goes.</summary>
        public ExecuteReport Execute(IProgress<DeleteProgress> progress = null)
        {
            var report = Session.Execute(CreateFileHandler(), progress);
            InvalidateCache();
            DataChanged?.Invoke();
            return report;
        }

        /// <summary>A video in the current results (for playing it), or null.</summary>
        public VideoEntry FindEntry(int id) => Session.Results.AllEntries().FirstOrDefault(e => e.Id == id);

        /// <summary>
        /// Marks a copy as not a duplicate of the other copies in its group: the pairs are saved in
        /// falsepositives.json (by content, so moving the files doesn't matter) and the copy leaves the group.
        /// Returns a message for the user.
        /// </summary>
        public string MarkFalsePositive(int id)
        {
            var group = Session.GroupOf(id);
            if (group == null) return "This video is not in a group of copies.";
            var ids = group.Members.Select(m => m.Id).ToHashSet();
            var videos = CachedMedia().Where(v => ids.Contains(v.id)).ToDictionary(v => v.id);
            if (!videos.TryGetValue(id, out var video)) return "This video is no longer in the database. Scan again first.";
            var others = group.Members.Where(m => m.Id != id && videos.ContainsKey(m.Id)).Select(m => videos[m.Id]).ToList();
            FalsePositives.Add(video, others);
            Session.RemoveFromGroup(id);
            return $"Marked {System.IO.Path.GetFileName(video.path)} as not a duplicate. It won't be matched with these copies again.";
        }

        /// <summary>
        /// The marked pairs, newest first, with where each video is now (found by its fingerprint in the
        /// database, so moved files show their new place; null when it is no longer in the database).
        /// </summary>
        public List<FalsePositiveDetail> FalsePositiveDetails()
        {
            var all = FalsePositives.All();
            if (all.Count == 0) return new();
            var wanted = all.SelectMany(p => new[] { p.A, p.B }).ToHashSet();
            var found = new Dictionary<string, VideoEntry>();
            foreach (var v in CachedMedia())
            {
                var print = FalsePositives.Fingerprint(v);
                if (wanted.Contains(print) && !found.ContainsKey(print)) found[print] = VideoEntry.From(v);
            }
            return all.Select(p => new FalsePositiveDetail(p,
                found.TryGetValue(p.A, out var a) ? a : null,
                found.TryGetValue(p.B, out var b) ? b : null)).ToList();
        }

        /// <summary>Forgets a false positive; "Compare again" can then find the pair again.</summary>
        public bool RemoveFalsePositive(string key) => FalsePositives.Remove(key);

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

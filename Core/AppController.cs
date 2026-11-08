using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace deepduplicates
{
    /// <summary>The encoding script: where it was written (null when nothing was left), how many videos, and the missing files left out.</summary>
    public sealed record EncodeScriptResult(string Path, int Videos, List<EncodeCandidate> Missing);

    /// <summary>What happened when videos were added to the encode queue.</summary>
    public sealed record EncodeQueueResult(int Added, int AlreadyThere, List<EncodeCandidate> Missing);

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
        private List<VideoInfo> currentMedia;
        private FileIndex fileIndex;
        private readonly object cacheLock = new();

        public string DataFolder { get; }
        public Settings Settings { get; private set; }
        /// <summary>True when settings.json did not exist and was created with defaults.</summary>
        public bool IsFirstRun { get; }
        public ScanEngine Scanner { get; } = new();
        /// <summary>The encode queue (Re-encode adds videos, the Re-encode queue screen shows it).</summary>
        public EncodeEngine Encoder { get; }
        public ResultSession Session { get; private set; }

        /// <summary>Raised when there is a new review session (after a scan, or new results).</summary>
        public event Action SessionReplaced;

        /// <summary>Raised when videos in the database changed (scan finished, files removed) - previews are outdated.</summary>
        public event Action DataChanged;

        /// <summary>Raised when Review added a path to skip to the saved settings (screens holding a copy add it too).</summary>
        public event Action<string> ExcludeAdded;

        private AppController(string dataFolder)
        {
            DataFolder = dataFolder;
            Settings = FileHandler.LoadSettings(out bool created);
            IsFirstRun = created;
            // Rules written with a synonym's alias (T:\) follow the main name, so none are left behind
            try { if (UseMainNames(Settings)) FileHandler.SaveSettings(Settings); }
            catch (Exception e) { Console.WriteLine("Could not update the rules to the main names: " + e.Message); }
            Session = new ResultSession(ResultSet.LoadSaved());
            Encoder = new EncodeEngine(Path.Combine(FileHandler.AppFolder, "output", "encodequeue.json"), PrepareFFmpeg);
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
            UseMainNames(settings);
            FileHandler.SaveSettings(settings);
            Settings = settings.Clone();
        }

        /// <summary>
        /// Folders to scan, paths to skip and preferred folders that start with a synonym's alias are changed to its
        /// main name (what the database uses). Returns true when something changed.
        /// </summary>
        private static bool UseMainNames(Settings settings)
        {
            int changed = 0;
            foreach (var s in PathMapper.Synonyms) changed += SynonymTools.UpdateSettings(settings, s);
            return changed > 0;
        }

        /// <summary>Starts a scan with the saved settings.</summary>
        public bool StartScan() => Scanner.Start(Settings);

        /// <summary>
        /// Compares again with the saved settings and the videos already read (no rescan). Only the files the
        /// last scan found on disk take part: the database also holds dormant rows of moved or missing files.
        /// </summary>
        public void ReanalyzeFromDatabase() => ReanalyzeFromDatabase(null);

        /// <summary>As above, reporting the steps (loading the videos, comparing with its share done, saving).</summary>
        public void ReanalyzeFromDatabase(IProgress<SynonymProgress> progress)
        {
            progress?.Report(new SynonymProgress("Loading the videos", null));
            var media = ScanEngine.InScope(CurrentMedia(), Settings);
            progress?.Report(new SynonymProgress("Comparing the videos", 0));
            var (results, _) = Analyzer.Analyze(media, Settings, progress == null ? null : done => progress.Report(new SynonymProgress($"Comparing the videos ({done:P0})", done)));
            progress?.Report(new SynonymProgress("Saving the results", null));
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

        /// <summary>
        /// Every video the last scan found (the All files screen), indexed for searching. Built once and kept
        /// until the data changes; the database's dormant rows are left out (see CurrentMedia). Needs no scan:
        /// the files come from the list the last scan saved, the details from the database.
        /// </summary>
        public FileIndex AllFiles()
        {
            lock (cacheLock)
            {
                if (fileIndex != null) return fileIndex;
                // With a saved list: only the columns the list shows - no scan, no fingerprints (about a second)
                var listed = LastScan.FilterEntries(ScanEngine.LoadEntries());
                return fileIndex = new FileIndex(listed ?? CurrentMedia().Where(v => !string.IsNullOrEmpty(v.path)).Select(VideoEntry.From).ToList());
            }
        }

        /// <summary>A video in the current results or found by the last scan (for playing it), or null.</summary>
        public VideoEntry FindEntry(int id)
        {
            var entry = Session.Results.AllEntries().FirstOrDefault(e => e.Id == id);
            if (entry != null) return entry;
            // Re-encode lists the other videos the last scan found
            var video = CurrentMedia().FirstOrDefault(v => v.id == id);
            return video == null ? null : VideoEntry.From(video);
        }

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
            return $"Marked {System.IO.Path.GetFileName(video.path)} as a false positive. It won't be matched with these copies again.";
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
            // Files found by the last scan first, so a moved video shows where it is now, not a dormant row
            foreach (var v in CurrentMedia().Concat(CachedMedia()))
            {
                var print = FalsePositives.Fingerprint(v);
                if (wanted.Contains(print) && !found.ContainsKey(print)) found[print] = VideoEntry.From(v);
            }
            return all.Select(p => new FalsePositiveDetail(p,
                found.TryGetValue(p.A, out var a) ? a : null,
                found.TryGetValue(p.B, out var b) ? b : null)).ToList();
        }

        /// <summary>
        /// "Exclude folder" in Review: adds the folder (with its sub folders) to the paths to skip in the
        /// settings, so later scans leave it out, and takes its files out of the current results.
        /// Returns a message for the user.
        /// </summary>
        public string ExcludeFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "No folder chosen.";
            // With the separator, "D:\Videos\a" does not also skip "D:\Videos\ab"
            string rule = folder.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            var settings = Settings.Clone();
            if (!settings.excludePaths.Contains(rule, StringComparer.OrdinalIgnoreCase))
            {
                settings.excludePaths = settings.excludePaths.Append(rule).ToArray();
                settings.Normalize();
                FileHandler.SaveSettings(settings);
                Settings = settings;
                ExcludeAdded?.Invoke(rule);
            }
            int n = Session.RemoveUnder(folder);
            return $"Excluded {folder} - {Fmt.Plural(n, "file", "files")} taken out of the matches. Undo it under Settings, paths to skip.";
        }

        /// <summary>Forgets a false positive; "Compare again" can then find the pair again.</summary>
        public bool RemoveFalsePositive(string key) => FalsePositives.Remove(key);

        /// <summary>
        /// Videos in the scope that could be re-encoded (not duplicates or broken), highest average bitrate first.
        /// Only files the last scan found on disk (see CurrentMedia); the picked ones are checked again when the
        /// script is written.
        /// </summary>
        public List<EncodeCandidate> EncodeCandidates()
        {
            var media = ScanEngine.InScope(CurrentMedia(), Settings);
            var (_, analyzed) = Analyzer.Analyze(media, Settings);
            return EncodePlanner.Candidates(analyzed, Settings.EncodeOrDefault());
        }

        /// <summary>
        /// Writes output/encode_selected.ps1 (Windows) or .sh (Linux) for these videos. Files that no longer
        /// exist are left out and counted in Missing.
        /// </summary>
        public EncodeScriptResult WriteEncodeScript(IEnumerable<EncodeCandidate> videos)
        {
            var list = videos.ToList();
            var exists = LastScan.Exist(list.Select(c => c.Path).ToList());
            var present = list.Where((c, i) => exists[i]).ToList();
            var missing = list.Where((c, i) => !exists[i]).ToList();
            string path = present.Count == 0 ? null
                : EncodePlanner.WriteScript(CreateFileHandler().outputFolder, EncodeScriptName, present, Settings.EncodeOrDefault());
            return new EncodeScriptResult(path, present.Count, missing);
        }

        public const string EncodeScriptName = "encode_selected";

        /// <summary>
        /// Adds videos to the encode queue (it starts by itself unless it was stopped), with the saved encode
        /// settings. Files that no longer exist are not added and are counted in Missing.
        /// </summary>
        public EncodeQueueResult EnqueueEncode(IEnumerable<EncodeCandidate> videos)
        {
            var list = videos.ToList();
            var exists = LastScan.Exist(list.Select(c => c.Path).ToList());
            var present = list.Where((c, i) => exists[i]).ToList();
            var missing = list.Where((c, i) => !exists[i]).ToList();
            var added = present.Count == 0 ? new EnqueueResult(0, 0) : Encoder.Enqueue(present, Settings.EncodeOrDefault());
            return new EncodeQueueResult(added.Added, added.AlreadyThere, missing);
        }

        /// <summary>The result of an action on a finished video in the encode queue: an error, or what was done.</summary>
        public sealed record EncodeActionResult(string Error, string Message, bool Recoverable = false)
        {
            public bool Ok => Error.Length == 0;
        }

        /// <summary>True when deleting this file puts it in the Recycle Bin (trash) - for the confirmation text.</summary>
        public bool CanRecover(string path) => RecycleBin.IsRecoverable(path);

        /// <summary>
        /// Deletes the original of a video whose new file has been checked (length and pictures, see EncodeChecker): to the
        /// Recycle Bin, or for good where there is none (a network share) - the interface says which before asking.
        /// Refuses unless the check passed and the new file is still the one that was checked.
        /// </summary>
        public EncodeActionResult DeleteOriginalAfterEncode(string itemId)
        {
            var item = Encoder.Find(itemId);
            if (item == null) return new("This video is not in the list any more.", "");
            if (!item.CanDeleteOriginal) return new("The new file has not been checked, so the original is kept.", "");
            try
            {
                var created = new FileInfo(item.OutputPath);
                if (!created.Exists || created.Length != item.OutputBytes)
                    return new("The new file is not there or has changed since it was checked. The original is kept.", "");
                if (!File.Exists(item.Path))
                {
                    RemoveDeletedVideo(item.VideoId, item.Path);
                    Encoder.MarkOriginalDeleted(item.Id);
                    return new("", "The original was already gone.");
                }
                bool recoverable = RecycleBin.Delete(item.Path);
                RemoveDeletedVideo(item.VideoId, item.Path);
                Encoder.MarkOriginalDeleted(item.Id);
                return new("", recoverable
                    ? $"Moved {Path.GetFileName(item.Path)} to the Recycle Bin ({Fmt.Size(item.SizeBytes)})."
                    : $"Deleted {Path.GetFileName(item.Path)} ({Fmt.Size(item.SizeBytes)}) permanently: this location has no Recycle Bin.", recoverable);
            }
            catch (Exception e) { return new($"Could not delete {Path.GetFileName(item.Path)}: {e.Message}", ""); }
        }

        /// <summary>
        /// For a video whose new file is incomplete (an earlier run was cut off): moves that file out of the way and puts the
        /// video in the queue again. Only ever touches the new file recorded for that video.
        /// </summary>
        public EncodeActionResult ReplaceIncomplete(string itemId)
        {
            var item = Encoder.Find(itemId);
            if (item == null) return new("This video is not in the list any more.", "");
            if (!item.CanReplace) return new("The new file is not marked as incomplete, so it is kept.", "");
            try
            {
                bool recoverable = RecycleBin.Delete(item.OutputPath);
                Encoder.Retry(item.Id);
                return new("", $"Removed the incomplete {Path.GetFileName(item.OutputPath)} ({Fmt.Size(item.OutputBytes)}"
                    + (recoverable ? ", in the Recycle Bin" : ", deleted for good") + ") and queued the video again.", recoverable);
            }
            catch (Exception e) { return new($"Could not remove {Path.GetFileName(item.OutputPath)}: {e.Message}", ""); }
        }

        // The program's own ffmpeg is used when it is there; only a missing one is fetched (works offline afterwards)
        private static async System.Threading.Tasks.Task<string> PrepareFFmpeg()
        {
            if (!File.Exists(FileHandler.FFmpegPath)) await FileHandler.EnsureFFmpegAsync();
            return FileHandler.FFmpegPath;
        }

        /// <summary>
        /// Rewrites the stored paths under the synonym's alias to its main name (see SynonymTools.Update).
        /// Not while a scan is running. Compares again afterwards, so the review uses the new paths.
        /// </summary>
        public SynonymUpdateReport UpdateSynonymPaths(PathSynonym synonym, IProgress<SynonymProgress> progress = null)
        {
            ScanLock scanLock;
            try { scanLock = ScanLock.Acquire("updating paths"); }
            catch (ScanAlreadyRunningException e) { return new SynonymUpdateReport(0, 0, e.Message); }
            using (scanLock)
            {
                var report = SynonymTools.Update(synonym, progress);
                // The saved rules and the last scan's file list use the new name too
                progress?.Report(new SynonymProgress("Updating the settings and the file list", null));
                var settings = Settings.Clone();
                int rules = SynonymTools.UpdateSettings(settings, synonym);
                if (rules > 0)
                {
                    settings.Normalize();
                    FileHandler.SaveSettings(settings);
                    Settings = settings;
                    report = report with { Message = report.Message + $" {Fmt.Plural(rules, "rule", "rules")} in the settings (folders to scan, paths to skip, preferred folders) updated too." };
                }
                LastScan.Rename(p => SynonymTools.RenameRoot(p, synonym));
                InvalidateCache();
                ReanalyzeFromDatabase(progress);
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

        /// <summary>
        /// The database rows of the files the last scan found on disk. The database itself keeps dormant rows
        /// (moved, renamed or unreachable files) and must not be taken as the list of files that exist.
        /// </summary>
        private List<VideoInfo> CurrentMedia()
        {
            // Kept with the cache: without a scan list, the fallback checks every row on disk, which is slow on shares
            lock (cacheLock) return currentMedia ??= LastScan.Filter(CachedMedia());
        }

        /// <summary>
        /// Deletes one video file (from Re-encode) and takes it out of the database and the scan list.
        /// Returns null when it worked, otherwise the reason.
        /// </summary>
        public string DeleteVideoFile(int id, string path)
        {
            try
            {
                VideoFile.Delete(path);
            }
            catch (Exception e) { return e.Message; }
            RemoveDeletedVideo(id, path);
            return null;
        }

        // A video file is gone: out of the database (with its screenshots), the scan list and the caches
        private void RemoveDeletedVideo(int id, string path)
        {
            ResultSession.RemoveFromDatabase(new List<VideoEntry> { new VideoEntry { Id = id, Path = path } }, CreateFileHandler());
            LastScan.Remove(path);
            InvalidateCache();
        }

        private void InvalidateCache()
        {
            lock (cacheLock) { cachedMedia = null; currentMedia = null; fileIndex = null; }
        }

        private void ReplaceSession(ResultSet results)
        {
            Session = new ResultSession(results);
            SessionReplaced?.Invoke();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace deepduplicates
{
    /// <summary>
    /// Runs a full scan (find files, read new videos, compare) for the interfaces, reporting progress.
    /// Only one scan runs at a time.
    /// </summary>
    public sealed class ScanEngine
    {
        private readonly object sync = new();
        private CancellationTokenSource cts;

        public ScanProgress Current { get; private set; } = new ScanProgress { Phase = ScanPhase.Idle };

        /// <summary>Shown to others while this program scans ("the web app", "the desktop app").</summary>
        public string ProgramName { get; set; } = "this program";
        public bool IsRunning { get; private set; }

        /// <summary>Raised on every progress update (from a background thread).</summary>
        public event Action<ScanProgress> ProgressChanged;

        /// <summary>Raised when a scan has finished with new results.</summary>
        public event Action<ResultSet> Completed;

        public void Cancel() => cts?.Cancel();

        /// <summary>Starts a scan in the background. Returns false if one is already running.</summary>
        public bool Start(Settings settings)
        {
            lock (sync)
            {
                if (IsRunning) return false;
                IsRunning = true;
                cts = new CancellationTokenSource();
            }
            var token = cts.Token;
            Task.Run(async () =>
            {
                try { await RunAsync(settings.Clone(), token); }
                finally { lock (sync) IsRunning = false; }
            });
            return true;
        }

        private void Report(ScanProgress p)
        {
            Current = p;
            ProgressChanged?.Invoke(p);
        }

        public async Task<ResultSet> RunAsync(Settings settings, CancellationToken ct)
        {
            var total = Stopwatch.StartNew();
            var record = new RunRecord
            {
                ScopeKey = RunHistory.ScopeKey(settings),
                Machine = Environment.MachineName,
                StartedUtc = DateTime.UtcNow
            };
            var videoHandler = (VideoHandler)null;
            ScanLock scanLock = null;
            try
            {
                scanLock = ScanLock.Acquire("a scan from " + ProgramName);
                Report(new ScanProgress { Phase = ScanPhase.Preparing, Message = "Checking FFmpeg" });
                await FileHandler.EnsureFFmpegAsync();

                var fileHandler = new FileHandler(settings);
                Report(new ScanProgress { Phase = ScanPhase.FindingFiles, Message = "Finding video files" });
                fileHandler.FindFiles(count => Report(new ScanProgress
                {
                    Phase = ScanPhase.FindingFiles,
                    Message = "Finding video files",
                    FilesFound = count,
                    Elapsed = total.Elapsed
                }), ct);
                record.FilesInScope = fileHandler.allFilesCount;
                // What is on disk now - the database also holds dormant rows of moved or missing files
                try { LastScan.Save(fileHandler.allFiles); } catch (Exception e) { Console.WriteLine("Could not save lastscan.json: " + e.Message); }

                using var db = new VideoInfoContext();
                db.Database.EnsureCreated();
                videoHandler = new VideoHandler(fileHandler, new ImageHandler());
                var progress = new Progress(p => Report(p with { Elapsed = total.Elapsed }));
                var media = await videoHandler.saveVideoMetadataAndScreenshots(db, progress, ct, RunHistory.SecondsPerNewFile(settings));

                Report(Current with { Phase = ScanPhase.Comparing, Message = "Comparing videos", Remaining = null, Elapsed = total.Elapsed });
                var (results, analyzed) = Analyzer.Analyze(media, settings);
                results.Save();

                // The command-line outputs are still written, for anyone who prefers them
                try
                {
                    fileHandler.generateBatchFile(analyzed);
                    fileHandler.generateReport(analyzed);
                }
                catch (Exception e) { Console.WriteLine("Could not write report/script: " + e.Message); }

                record.Completed = true;
                Report(Current with
                {
                    Phase = ScanPhase.Done,
                    Message = $"Found {results.DuplicateCount} duplicates and {results.Broken.Count} broken or short videos",
                    Remaining = TimeSpan.Zero,
                    Elapsed = total.Elapsed
                });
                Completed?.Invoke(results);
                return results;
            }
            catch (OperationCanceledException)
            {
                Report(Current with { Phase = ScanPhase.Cancelled, Message = "Stopped. What was read is saved - the next scan continues from there.", Remaining = null, Elapsed = total.Elapsed });
                return null;
            }
            catch (ScanAlreadyRunningException e)
            {
                Report(new ScanProgress { Phase = ScanPhase.Failed, Message = e.Message });
                return null;
            }
            catch (Exception e)
            {
                Report(Current with { Phase = ScanPhase.Failed, Message = e.Message, Remaining = null, Elapsed = total.Elapsed });
                return null;
            }
            finally
            {
                // No history entry when the scan never started (another scan held the lock)
                if (scanLock != null)
                {
                    if (videoHandler != null)
                    {
                        record.NewFiles = videoHandler.LastNewFiles;
                        record.NewFileSeconds = videoHandler.LastNewSeconds;
                    }
                    record.TotalSeconds = total.Elapsed.TotalSeconds;
                    try { RunHistory.Add(record); } catch { }
                    scanLock.Dispose();
                }
            }
        }

        /// <summary>
        /// Loads the videos of the scope that are already in the database (with their screenshot hashes),
        /// without touching the video files. Used for the live preview and for opening results quickly.
        /// </summary>
        public static List<VideoInfo> LoadCached(Settings settings) => InScope(LoadAllCached(), settings);

        /// <summary>Every video in the database, with screenshot hashes decoded.</summary>
        public static List<VideoInfo> LoadAllCached()
        {
            using var db = new VideoInfoContext();
            db.Database.EnsureCreated();
            var images = new ImageHandler();
            var list = new List<VideoInfo>();
            foreach (var v in db.VideoInfos.AsNoTracking().AsEnumerable())
            {
                if (v.path == null) continue;
                if (v.image1hash_blob != null) v.image1hash = images.ByteArrayToImageHash(v.image1hash_blob);
                if (v.image2hash_blob != null) v.image2hash = images.ByteArrayToImageHash(v.image2hash_blob);
                if (v.image3hash_blob != null) v.image3hash = images.ByteArrayToImageHash(v.image3hash_blob);
                list.Add(v);
            }
            return list;
        }

        /// <summary>The videos inside the content folders and not excluded.</summary>
        public static List<VideoInfo> InScope(List<VideoInfo> all, Settings settings)
        {
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var roots = settings.contentFolders.Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.TrimEnd('/', '\\') + Path.DirectorySeparatorChar).ToList();
            var excludes = settings.excludePaths.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return all.Where(v => roots.Any(r => v.path.StartsWith(r, cmp)) && !excludes.Any(x => v.path.Contains(x))).ToList();
        }

        private sealed class Progress : IProgress<ScanProgress>
        {
            private readonly Action<ScanProgress> action;
            public Progress(Action<ScanProgress> action) => this.action = action;
            public void Report(ScanProgress value) => action(value);
        }
    }
}

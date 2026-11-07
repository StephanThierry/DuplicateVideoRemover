#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace deepduplicates
{
    /// <summary>Runs FFmpeg for the encoder. A seam so the engine can be tested without FFmpeg.</summary>
    public interface IEncodeRunner
    {
        /// <summary>Runs the program and returns its exit code. When <paramref name="ct"/> is cancelled the process is killed and an OperationCanceledException thrown.</summary>
        Task<int> RunAsync(string program, IReadOnlyList<string> args, Action<string> onOutput, Action<string> onError, CancellationToken ct);
    }

    public sealed class ProcessEncodeRunner : IEncodeRunner
    {
        public async Task<int> RunAsync(string program, IReadOnlyList<string> args, Action<string> onOutput, Action<string> onError, CancellationToken ct)
        {
            var info = new ProcessStartInfo(program)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) info.ArgumentList.Add(a);   // no shell: nothing to quote
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) onError(e.Data); };
            process.Start();
            // Keep the computer usable while it encodes
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await process.WaitForExitAsync(CancellationToken.None);   // frees the file before the caller deletes it
                throw;
            }
            process.WaitForExit();   // the last output lines
            return process.ExitCode;
        }
    }

    /// <summary>One video in the queue, as kept in encodequeue.json.</summary>
    public sealed class EncodeItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int VideoId { get; set; }
        public string Path { get; set; } = "";
        public long SizeBytes { get; set; }
        public int DurationSeconds { get; set; }
        /// <summary>The settings the video was queued with (changing the settings later does not change what waits).</summary>
        public EncodeSettings Settings { get; set; } = EncodeSettings.CreateDefault();
        public string OutputPath { get; set; } = "";
        public EncodeItemStatus Status { get; set; }
        public string Message { get; set; } = "";
        public long OutputBytes { get; set; }
        public DateTime QueuedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }

        // While running (not saved)
        [System.Text.Json.Serialization.JsonIgnore] public double Fraction { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public double Fps { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public double Speed { get; set; }
    }

    /// <summary>What happened to the videos handed to <see cref="EncodeEngine.Enqueue"/>.</summary>
    public sealed record EnqueueResult(int Added, int AlreadyThere);

    /// <summary>
    /// The encode queue. Videos are added at any time (also while it runs) and encoded one by one with FFmpeg,
    /// reporting progress. The queue is saved in encodequeue.json, so it survives a restart: after one, it waits
    /// for <see cref="Start"/> (a server can call it at start-up). Each video is written to "name.mp4.part" and
    /// only gets its real name when FFmpeg has finished, so a stop, crash or scan never meets half a video.
    /// </summary>
    public sealed class EncodeEngine
    {
        private const int KeepFinished = 300, KeepLog = 300;

        private readonly object sync = new();
        private readonly List<EncodeItem> items = new();
        private readonly List<string> log = new();
        private readonly string stateFile;
        private readonly IEncodeRunner runner;
        private readonly Func<Task<string>> prepareFfmpeg;
        private CancellationTokenSource? workerCts, itemCts;
        private Task? worker;
        private bool halted, running;
        private string error = "";

        // The run: from the first video after idle until the queue is empty
        private DateTime runStartUtc;
        private DateTime? runEndUtc;
        private double encodedMediaSeconds, encodeWallSeconds;

        public EncodeProgress Current { get; private set; } = new();

        /// <summary>Shown to others while this program encodes ("the web app", "the desktop app").</summary>
        public string ProgramName { get; set; } = "this program";
        public bool IsRunning { get { lock (sync) return running; } }

        /// <summary>Raised on every progress update (from a background thread).</summary>
        public event Action<EncodeProgress>? ProgressChanged;

        /// <param name="stateFile">Where the queue is saved</param>
        /// <param name="prepareFfmpeg">Makes sure FFmpeg is there and returns the path of ffmpeg</param>
        public EncodeEngine(string stateFile, Func<Task<string>> prepareFfmpeg, IEncodeRunner? runner = null)
        {
            this.stateFile = stateFile;
            this.prepareFfmpeg = prepareFfmpeg;
            this.runner = runner ?? new ProcessEncodeRunner();
            Load();
            Current = Snapshot();
        }

        // ---------- Commands ----------

        /// <summary>
        /// Adds videos to the end of the queue and starts encoding, unless the queue was stopped. A video that is
        /// already waiting, running or done is not added again; one that failed or was skipped is queued afresh.
        /// </summary>
        public EnqueueResult Enqueue(IEnumerable<EncodeCandidate> videos, EncodeSettings settings)
        {
            int added = 0, there = 0;
            lock (sync)
            {
                foreach (var c in videos)
                {
                    var existing = items.FirstOrDefault(i => EncodeProgress.PathComparer.Equals(i.Path, c.Path));
                    if (existing != null && existing.Status is EncodeItemStatus.Queued or EncodeItemStatus.Running or EncodeItemStatus.Done) { there++; continue; }
                    if (existing != null) items.Remove(existing);
                    var s = settings.Clone();
                    items.Add(new EncodeItem
                    {
                        VideoId = c.Id, Path = c.Path, SizeBytes = c.SizeBytes, DurationSeconds = c.DurationSeconds,
                        Settings = s, OutputPath = EncodePlanner.OutputPath(c.Path, s), Status = EncodeItemStatus.Queued,
                    });
                    added++;
                }
                if (added > 0) Save();
            }
            if (added > 0 && !halted) StartWorker();
            Publish();
            return new EnqueueResult(added, there);
        }

        /// <summary>Starts (or resumes) encoding what is waiting.</summary>
        public void Start()
        {
            lock (sync) { halted = false; error = ""; }
            StartWorker();
            Publish();
        }

        /// <summary>Stops after putting the running video back in the queue (its partial file is deleted). The rest keeps waiting.</summary>
        public void Stop()
        {
            lock (sync) { halted = true; workerCts?.Cancel(); }
            Publish();
        }

        /// <summary>Gives up on the running video (marked as skipped) and goes on with the next.</summary>
        public void SkipCurrent()
        {
            lock (sync) itemCts?.Cancel();
        }

        /// <summary>Takes a waiting video out of the queue.</summary>
        public bool Remove(string id)
        {
            lock (sync)
            {
                var item = items.FirstOrDefault(i => i.Id == id && i.Status == EncodeItemStatus.Queued);
                if (item == null) return false;
                items.Remove(item);
                Save();
            }
            Publish();
            return true;
        }

        /// <summary>Puts a failed or skipped video at the end of the queue again.</summary>
        public bool Retry(string id)
        {
            lock (sync)
            {
                var item = items.FirstOrDefault(i => i.Id == id && i.Status is EncodeItemStatus.Failed or EncodeItemStatus.Skipped);
                if (item == null) return false;
                items.Remove(item);
                item.Status = EncodeItemStatus.Queued; item.Message = ""; item.FinishedUtc = null; item.StartedUtc = null; item.OutputBytes = 0;
                item.QueuedUtc = DateTime.UtcNow;
                items.Add(item);
                Save();
            }
            if (!halted) StartWorker();
            Publish();
            return true;
        }

        /// <summary>Takes every waiting video out of the queue.</summary>
        public int ClearQueued()
        {
            int n;
            lock (sync) { n = items.RemoveAll(i => i.Status == EncodeItemStatus.Queued); if (n > 0) Save(); }
            Publish();
            return n;
        }

        /// <summary>Takes the finished videos (done, failed, skipped) out of the list; the Re-encode list shows them normally again.</summary>
        public int ClearFinished()
        {
            int n;
            lock (sync) { n = items.RemoveAll(i => i.Status is EncodeItemStatus.Done or EncodeItemStatus.Failed or EncodeItemStatus.Skipped); if (n > 0) Save(); }
            Publish();
            return n;
        }

        /// <summary>Stops and waits for the running FFmpeg to be killed and its partial file deleted (when the program closes).</summary>
        public void Shutdown()
        {
            Task? w;
            lock (sync) { halted = true; workerCts?.Cancel(); w = worker; }
            try { w?.Wait(TimeSpan.FromSeconds(10)); } catch { }
        }

        // ---------- The worker ----------

        private void StartWorker()
        {
            lock (sync)
            {
                if (running || !items.Any(i => i.Status == EncodeItemStatus.Queued)) return;
                running = true;
                workerCts = new CancellationTokenSource();
                var token = workerCts.Token;
                runStartUtc = DateTime.UtcNow; runEndUtc = null; encodedMediaSeconds = encodeWallSeconds = 0;
                worker = Task.Run(() => WorkAsync(token));
            }
        }

        private async Task WorkAsync(CancellationToken ct)
        {
            ScanLock? encodeLock = null;
            bool again;
            try
            {
                encodeLock = ScanLock.Acquire(ProgramName, Path.Combine(Path.GetDirectoryName(stateFile)!, "encode.lock"),
                    who => $"Another program is already encoding with this data folder ({who}). Wait for it to finish.");
                string ffmpeg = await prepareFfmpeg();
                while (!ct.IsCancellationRequested)
                {
                    EncodeItem? item;
                    lock (sync)
                    {
                        item = items.FirstOrDefault(i => i.Status == EncodeItemStatus.Queued);
                        if (item == null) break;
                        item.Status = EncodeItemStatus.Running; item.StartedUtc = DateTime.UtcNow; item.FinishedUtc = null;
                        item.Fraction = item.Fps = item.Speed = 0; item.OutputBytes = 0; item.Message = "";
                        itemCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        Save();
                    }
                    AddLog($"Started {Path.GetFileName(item.Path)} ({Fmt.Size(item.SizeBytes)}, {Fmt.Duration(item.DurationSeconds)})");
                    Publish();
                    await RunItemAsync(item, ffmpeg, itemCts!.Token, ct);
                    lock (sync) { itemCts?.Dispose(); itemCts = null; }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lock (sync) { error = e.Message; halted = true; }
                AddLog("Stopped: " + e.Message);
            }
            finally
            {
                encodeLock?.Dispose();
                lock (sync)
                {
                    // A video that was running when the worker stopped goes back in the queue
                    foreach (var i in items.Where(i => i.Status == EncodeItemStatus.Running)) { i.Status = EncodeItemStatus.Queued; i.Fraction = 0; }
                    runEndUtc = DateTime.UtcNow;
                    running = false;
                    Save();
                    // Videos added just as the queue ran dry still get their turn
                    again = !halted && items.Any(i => i.Status == EncodeItemStatus.Queued);
                }
                Publish();
                if (again) StartWorker();
            }
        }

        private async Task RunItemAsync(EncodeItem item, string ffmpeg, CancellationToken itemToken, CancellationToken workerToken)
        {
            var s = item.Settings;
            string output = item.OutputPath, part = EncodePlanner.PartPath(output);
            var wall = Stopwatch.StartNew();
            var tail = new Queue<string>();
            try
            {
                if (!File.Exists(item.Path)) { Finish(item, EncodeItemStatus.Skipped, "the video is not there any more"); return; }
                if (s.neverOverwrite && File.Exists(output)) { Finish(item, EncodeItemStatus.Skipped, "the new file already exists"); return; }
                if (FreeSpaceProblem(item) is string problem) { Finish(item, EncodeItemStatus.Failed, problem); return; }
                TryDelete(part);

                var parser = new FfmpegProgressParser();
                var args = EncodePlanner.Arguments(item.Path, s, part);
                int exit = await runner.RunAsync(ffmpeg, args,
                    line =>
                    {
                        if (!parser.Feed(line)) return;
                        lock (sync)
                        {
                            item.Fraction = parser.FractionOf(item.DurationSeconds);
                            item.Fps = parser.Fps; item.Speed = parser.Speed; item.OutputBytes = parser.TotalSize;
                        }
                        Publish();
                    },
                    line =>
                    {
                        lock (sync) { tail.Enqueue(line); while (tail.Count > 12) tail.Dequeue(); }
                        AddLog(line, publish: false);
                    }, itemToken);

                if (exit != 0)
                {
                    string why = tail.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "no message";
                    TryDelete(part);
                    Finish(item, EncodeItemStatus.Failed, $"FFmpeg stopped with code {exit} ({why.Trim()})");
                    return;
                }
                if (!File.Exists(part) || new FileInfo(part).Length == 0) { TryDelete(part); Finish(item, EncodeItemStatus.Failed, "FFmpeg made no file"); return; }
                if (s.neverOverwrite && File.Exists(output)) { TryDelete(part); Finish(item, EncodeItemStatus.Failed, "the new file appeared meanwhile, nothing was overwritten"); return; }
                File.Move(part, output, overwrite: true);
                lock (sync)
                {
                    item.OutputBytes = new FileInfo(output).Length;
                    item.Fraction = 1;
                    encodedMediaSeconds += item.DurationSeconds;
                }
                Finish(item, EncodeItemStatus.Done, "");
            }
            catch (OperationCanceledException)
            {
                TryDelete(part);
                if (workerToken.IsCancellationRequested)
                {
                    // Stopped: the video waits for the next start
                    lock (sync) { item.Status = EncodeItemStatus.Queued; item.Fraction = item.Fps = item.Speed = 0; item.OutputBytes = 0; Save(); }
                    AddLog($"Stopped {Path.GetFileName(item.Path)}; it stays in the queue");
                }
                else Finish(item, EncodeItemStatus.Skipped, "skipped by you");
            }
            catch (Exception e)
            {
                TryDelete(part);
                Finish(item, EncodeItemStatus.Failed, e.Message);
            }
            finally
            {
                lock (sync) encodeWallSeconds += wall.Elapsed.TotalSeconds;
            }
        }

        private void Finish(EncodeItem item, EncodeItemStatus status, string message)
        {
            lock (sync)
            {
                item.Status = status; item.Message = message; item.FinishedUtc = DateTime.UtcNow;
                if (status != EncodeItemStatus.Done) item.OutputBytes = 0;
                // Keep the list of finished videos from growing for ever
                var finished = items.Where(i => i.IsFinishedStatus()).OrderBy(i => i.FinishedUtc).ToList();
                foreach (var old in finished.Take(Math.Max(finished.Count - KeepFinished, 0))) items.Remove(old);
                Save();
            }
            string name = Path.GetFileName(item.Path);
            AddLog(status switch
            {
                EncodeItemStatus.Done => $"Finished {name}: {Fmt.Size(item.SizeBytes)} became {Fmt.Size(item.OutputBytes)}",
                EncodeItemStatus.Skipped => $"Skipped {name}: {message}",
                _ => $"Failed {name}: {message}",
            });
        }

        /// <summary>A message when the drive of the new file clearly has too little room, else null (also when it can't be checked, e.g. on a share).</summary>
        private static string? FreeSpaceProblem(EncodeItem item)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(item.OutputPath)) ?? "";
                if (root.Length == 0 || root.StartsWith(@"\\")) return null;
                long free = new DriveInfo(root).AvailableFreeSpace;
                long need = EncodeEstimator.EstimatedBytes(
                    new EncodeCandidate { Path = item.Path, SizeBytes = item.SizeBytes, DurationSeconds = item.DurationSeconds }, item.Settings);
                return free < need ? $"not enough free space on {root} (about {Fmt.Size(need)} needed, {Fmt.Size(free)} free)" : null;
            }
            catch { return null; }
        }

        private static void TryDelete(string path)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { if (File.Exists(path)) File.Delete(path); return; }
                catch (IOException) { Thread.Sleep(200); }   // FFmpeg may still be letting go of the file
                catch { return; }
            }
        }

        // ---------- Log, snapshot and saving ----------

        private void AddLog(string line, bool publish = true)
        {
            lock (sync)
            {
                log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
                if (log.Count > KeepLog) log.RemoveRange(0, log.Count - KeepLog);
            }
            if (publish) Publish();
        }

        private void Publish()
        {
            EncodeProgress snapshot;
            lock (sync) snapshot = Current = Snapshot();
            ProgressChanged?.Invoke(snapshot);
        }

        private EncodeProgress Snapshot()
        {
            lock (sync)
            {
                var infos = items.Select(i => new EncodeItemInfo(i.Id, i.VideoId, i.Path, i.SizeBytes, i.DurationSeconds,
                    EncodeText.Summary(i.Settings), i.OutputPath, i.Status, i.Message, i.OutputBytes, i.Fraction, i.Fps, i.Speed,
                    i.QueuedUtc, i.StartedUtc, i.FinishedUtc)).ToList();
                
                // This run: what waits or runs, plus what finished since it started
                var inRun = items.Where(i => !i.IsFinishedStatus() || i.FinishedUtc >= runStartUtc).ToList();
                double total = inRun.Sum(i => (double)i.DurationSeconds);
                double done = inRun.Sum(i => i.IsFinishedStatus() ? i.DurationSeconds : i.Status == EncodeItemStatus.Running ? i.DurationSeconds * i.Fraction : 0);
                double fraction = total > 0 ? Math.Clamp(done / total, 0, 1) : 0;

                // Time left: the speed of this run so far, in video seconds per second (the running video counts as far as it got)
                var cur = items.FirstOrDefault(i => i.Status == EncodeItemStatus.Running);
                double media = encodedMediaSeconds + (cur != null ? cur.DurationSeconds * cur.Fraction : 0);
                double wallTime = encodeWallSeconds + (cur?.StartedUtc is DateTime st ? (DateTime.UtcNow - st).TotalSeconds : 0);
                TimeSpan? remaining = running && media >= 20 && wallTime >= 5 && total > done
                    ? TimeSpan.FromSeconds((total - done) / (media / wallTime))
                    : null;

                var end = running ? DateTime.UtcNow : runEndUtc ?? runStartUtc;
                return new EncodeProgress
                {
                    Items = infos, Running = running, Halted = halted, Error = error, Log = log.ToList(),
                    Elapsed = runStartUtc == default ? TimeSpan.Zero : end - runStartUtc,
                    Remaining = remaining, Fraction = fraction,
                };
            }
        }

        private sealed class State { public List<EncodeItem> Items { get; set; } = new(); }

        // Callers hold the lock
        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
                string tmp = stateFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new State { Items = items }, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, stateFile, overwrite: true);
            }
            catch (Exception e) { Console.WriteLine("Could not save the encode queue: " + e.Message); }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(stateFile)) return;
                var state = JsonSerializer.Deserialize<State>(File.ReadAllText(stateFile));
                if (state == null) return;
                foreach (var i in state.Items)
                {
                    i.Settings ??= EncodeSettings.CreateDefault();
                    i.Settings.Normalize();
                    if (i.Status == EncodeItemStatus.Running)
                    {
                        // The program ended while this one ran: its partial file is useless
                        i.Status = EncodeItemStatus.Queued;
                        TryDelete(EncodePlanner.PartPath(i.OutputPath));
                    }
                    items.Add(i);
                }
                // After a restart nothing starts by itself (Start does, e.g. when a server asks for it)
                halted = items.Any(i => i.Status == EncodeItemStatus.Queued);
            }
            catch (Exception e) { Console.WriteLine("Could not read the encode queue: " + e.Message); }
        }
    }

    internal static class EncodeItemExtensions
    {
        public static bool IsFinishedStatus(this EncodeItem i) => i.Status is EncodeItemStatus.Done or EncodeItemStatus.Failed or EncodeItemStatus.Skipped;
    }
}

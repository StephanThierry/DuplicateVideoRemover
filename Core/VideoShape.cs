using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace deepduplicates
{
    /// <summary>
    /// The shape of a video's picture, as it is shown when played: width / height after the pixel shape (anamorphic DVDs) and the
    /// rotation (phone videos) - 1.78 for 16:9, 0.56 for a phone held upright.
    /// <para>
    /// The screenshots are all made 240 x 160 (3:2), whatever the video's shape: the comparison works on pictures of one size, and a
    /// picture squeezed to fill the frame has no black bars that would match between any two videos. To show them, they are drawn at
    /// the video's own shape inside the 3:2 frame (<see cref="FitPng"/>): the rest of the frame is left empty.
    /// </para>
    /// </summary>
    public static class VideoShape
    {
        /// <summary>The shape of the screenshots (240 x 160).</summary>
        public const double ShotAspect = 240.0 / 160.0;

        /// <summary>A stored shape: probed, but ffprobe could not tell (not probed again on the next scan).</summary>
        public const double Unknown = 0;

        /// <summary>The ffprobe arguments that print the first video stream's size, pixel shape and rotation.</summary>
        public static string[] ProbeArguments(string file) => new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height,sample_aspect_ratio:stream_tags=rotate:stream_side_data=rotation",
            "-of", "default=noprint_wrappers=1", file
        };

        /// <summary>The shape from ffprobe's output (<see cref="ProbeArguments"/>); <see cref="Unknown"/> when it cannot be told.</summary>
        public static double Parse(IEnumerable<string> lines)
        {
            double width = 0, height = 0, pixel = 1, rotation = 0;
            foreach (var raw in lines)
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string key = raw[..eq].Trim(), value = raw[(eq + 1)..].Trim();
                switch (key)
                {
                    case "width": double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out width); break;
                    case "height": double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out height); break;
                    case "sample_aspect_ratio": pixel = Ratio(value) is double r && r > 0 ? r : 1; break;   // "0:1" or "N/A": square pixels
                    case "rotation":                                                                           // newer files: the display matrix
                    case "TAG:rotate":                                                                         // older files: a tag
                        if (rotation == 0) double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out rotation);
                        break;
                }
            }
            if (width <= 0 || height <= 0) return Unknown;
            double aspect = width * pixel / height;
            // FFmpeg turns the picture upright when it takes a screenshot, so a quarter turn swaps width and height
            if (Math.Abs(Math.Abs(Math.Round(rotation / 90.0)) % 2 - 1) < 0.01) aspect = 1 / aspect;
            return aspect is > 0.1 and < 10 ? Math.Round(aspect, 4) : Unknown;
        }

        private static double? Ratio(string s)
        {
            var parts = s.Split(':', '/');
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) || b == 0) return null;
            return a / b;
        }

        /// <summary>Asks ffprobe (next to <paramref name="ffmpeg"/>) for the video's shape; <see cref="Unknown"/> when it cannot tell.</summary>
        public static async Task<double> ProbeAsync(string ffmpeg, string file, CancellationToken ct = default)
        {
            try
            {
                var info = new ProcessStartInfo(Subtitles.FfprobePath(ffmpeg))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in ProbeArguments(file)) info.ArgumentList.Add(a);
                using var process = Process.Start(info);
                if (process == null) return Unknown;
                using var reg = ct.Register(() => { try { process.Kill(); } catch { /* already gone */ } });
                var errors = process.StandardError.ReadToEndAsync();
                string output = await process.StandardOutput.ReadToEndAsync();
                await errors;
                await process.WaitForExitAsync();
                ct.ThrowIfCancellationRequested();
                return process.ExitCode == 0 ? Parse(output.Split('\n')) : Unknown;
            }
            catch (OperationCanceledException) { throw; }
            catch { return Unknown; }
        }

        /// <summary>True when a picture of this shape looks the same squeezed into the 3:2 frame (no need to redraw it).</summary>
        public static bool FitsAsIs(double aspect) => aspect <= 0 || Math.Abs(aspect / ShotAspect - 1) < 0.03;

        /// <summary>Where a picture of the given shape goes in a frame of <paramref name="width"/> x <paramref name="height"/>: as large as fits, centred.</summary>
        public static (double X, double Y, double Width, double Height) Fit(double aspect, double width, double height)
        {
            if (aspect <= 0) return (0, 0, width, height);
            double w = width, h = width / aspect;
            if (h > height) { h = height; w = height * aspect; }
            return ((width - w) / 2, (height - h) / 2, w, h);
        }

        /// <summary>
        /// The screenshot (a PNG, squeezed to 3:2) redrawn at the video's own shape inside the same frame, the rest transparent.
        /// Returns the picture as it is when the shape is unknown or (close to) 3:2, or when it cannot be read.
        /// </summary>
        public static byte[] FitPng(byte[] png, double aspect)
        {
            if (FitsAsIs(aspect)) return png;
            try
            {
                using var source = SKBitmap.Decode(png);
                if (source == null) return png;
                var box = Fit(aspect, source.Width, source.Height);
                using var surface = SKSurface.Create(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                using var image = SKImage.FromBitmap(source);
                canvas.DrawImage(image, SKRect.Create((float)box.X, (float)box.Y, (float)box.Width, (float)box.Height),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                using var snapshot = surface.Snapshot();
                using var data = snapshot.Encode(SKEncodedImageFormat.Png, 90);
                return data.ToArray();
            }
            catch { return png; }
        }

        /// <summary>Reads a screenshot and fits it to the shape (see <see cref="FitPng"/>); null when the file is not there.</summary>
        public static byte[]? ReadFitted(string path, double aspect)
        {
            try { return File.Exists(path) ? FitPng(File.ReadAllBytes(path), aspect) : null; }
            catch (IOException) { return null; }
        }
    }

    /// <summary>
    /// The shapes of the videos in the database, by id, for the pictures on screen. Loaded at the first picture, then again (in the background) when a
    /// video is asked for that is not known and the last load is more than half a minute old - so the shapes a scan reads (also one on
    /// another machine using the same database) show up without a restart.
    /// </summary>
    public static class VideoShapes
    {
        private static readonly object sync = new();
        private static Dictionary<int, double> known = new();
        private static DateTime loadedUtc = DateTime.MinValue;
        private static bool loading;

        /// <summary>Raised (on a background thread) when shapes were added: pictures already drawn may be drawn again.</summary>
        public static event Action? Changed;

        /// <summary>The video's shape, or <see cref="VideoShape.Unknown"/> (draw the picture as it is).</summary>
        public static double Of(int videoId)
        {
            bool reload;
            bool first;
            lock (sync) first = loadedUtc == DateTime.MinValue && !loading;
            if (first) Load();   // the first picture waits for the list (a few hundredths of a second), so the first page is drawn right
            lock (sync)
            {
                if (known.TryGetValue(videoId, out var aspect)) return aspect;
                reload = !loading && DateTime.UtcNow - loadedUtc > TimeSpan.FromSeconds(30);
                if (reload) loading = true;
            }
            if (reload) Task.Run(Load);
            return VideoShape.Unknown;
        }

        /// <summary>A shape the scan just read.</summary>
        public static void Remember(int videoId, double aspect)
        {
            if (videoId == 0 || aspect <= 0) return;
            lock (sync)
            {
                if (known.TryGetValue(videoId, out var had) && had == aspect) return;
                known[videoId] = aspect;
            }
            Changed?.Invoke();
        }

        /// <summary>Loads all shapes from the database now (also called by the first <see cref="Of"/> that misses).</summary>
        public static void Load()
        {
            try
            {
                using var db = new VideoInfoContext();
                var all = db.VideoInfos.Where(v => v.aspect > 0).Select(v => new { v.id, v.aspect }).ToList()
                    .ToDictionary(v => v.id, v => v.aspect!.Value);
                bool added;
                lock (sync)
                {
                    added = all.Any(p => !known.TryGetValue(p.Key, out var a) || a != p.Value);
                    foreach (var p in known) all.TryAdd(p.Key, p.Value);   // read by the scan, not saved yet
                    known = all;
                    loadedUtc = DateTime.UtcNow;
                }
                if (added) Changed?.Invoke();
            }
            catch (Exception e)
            {
                Console.WriteLine("Video shapes not loaded: " + e.Message);
                lock (sync) loadedUtc = DateTime.UtcNow;
            }
            finally
            {
                lock (sync) loading = false;
            }
        }
    }

    /// <summary>
    /// Reads the shape of the videos read before shapes were stored, in the background, a few at a time: only while no scan runs, the
    /// videos in the current results first. ffprobe only reads the start of a file (about 0.4 s a video over a network share), so a
    /// large library takes an hour or two, once. Each shape is saved at once, so stopping loses nothing.
    /// </summary>
    public static class ShapeFiller
    {
        private const int AtOnce = 4;

        /// <param name="ffmpeg">Where FFmpeg is (ffprobe is next to it); null while it is not there yet.</param>
        /// <param name="busy">True while a scan runs (it reads the shapes of the new videos itself).</param>
        /// <param name="first">The videos to do first (those in the results).</param>
        public static async Task RunAsync(Func<Task<string?>> ffmpeg, Func<bool> busy, Func<IReadOnlyCollection<int>> first, CancellationToken ct = default)
        {
            var passedOver = new HashSet<int>();   // files this machine cannot reach (another machine's paths): left for that one
            int done = 0;
            var watch = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (busy()) { await Task.Delay(TimeSpan.FromSeconds(20), ct); continue; }
                    var batch = NextBatch(first(), passedOver, 40);
                    if (batch.Count == 0)
                    {
                        if (done > 0) Console.WriteLine($"Picture shapes: read {done:N0} videos in {Fmt.Duration((int)watch.Elapsed.TotalSeconds)}");
                        done = 0;
                        await Task.Delay(TimeSpan.FromMinutes(10), ct);   // more may come from another machine's scan
                        watch.Restart();
                        continue;
                    }
                    if (await ffmpeg() is not string program) { await Task.Delay(TimeSpan.FromMinutes(1), ct); continue; }
                    var found = new System.Collections.Concurrent.ConcurrentDictionary<int, double>();
                    await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = ct }, async (v, token) =>
                    {
                        if (busy()) return;
                        if (!File.Exists(v.Path)) { lock (passedOver) passedOver.Add(v.Id); return; }
                        found[v.Id] = await VideoShape.ProbeAsync(program, v.Path, token);
                    });
                    using (var db = new VideoInfoContext())
                    {
                        foreach (var (id, shape) in found)
                            await db.VideoInfos.Where(x => x.id == id && x.aspect == null).ExecuteUpdateAsync(x => x.SetProperty(y => y.aspect, shape), ct);
                    }
                    foreach (var (id, shape) in found) VideoShapes.Remember(id, shape);
                    done += found.Count;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    Console.WriteLine("Picture shapes: " + e.Message);
                    try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
                }
            }
        }

        /// <summary>The next videos without a shape: those in <paramref name="first"/> before the rest.</summary>
        private static List<(int Id, string Path)> NextBatch(IReadOnlyCollection<int> first, HashSet<int> passedOver, int count)
        {
            using var db = new VideoInfoContext();
            var open = db.VideoInfos.Where(v => v.aspect == null && v.duration > 0 && !v.formatNotSupported && v.path != null);
            var ids = first.Where(id => !passedOver.Contains(id)).ToList();
            var batch = ids.Count > 0
                ? open.Where(v => ids.Contains(v.id)).Select(v => new { v.id, v.path }).Take(count).ToList()
                : new();
            if (batch.Count < count)
            {
                var skip = passedOver.ToList();
                batch.AddRange(open.Where(v => !skip.Contains(v.id) && !ids.Contains(v.id)).OrderByDescending(v => v.id)
                    .Select(v => new { v.id, v.path }).Take(count - batch.Count).ToList());
            }
            return batch.Select(v => (v.id, v.path)).ToList();
        }
    }
}

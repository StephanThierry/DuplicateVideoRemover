#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace deepduplicates
{
    /// <summary>The outcome of checking a re-encoded file.</summary>
    /// <param name="Valid">The file can be read in full: right length, and the pictures could be made and decoded.</param>
    /// <param name="Summary">What was checked, for a good file ("Same length (37:02), 3 pictures read").</param>
    /// <param name="Problem">What is wrong, for a bad file.</param>
    /// <param name="Pictures">How many of the pictures shown in the interface were made (0-3).</param>
    public sealed record EncodeCheckResult(bool Valid, string Summary, string Problem, int Pictures);

    /// <summary>
    /// Checks that a re-encoded file is complete and playable, the way the scan checks a video: read its length, then make
    /// pictures at 25%, 50% and 75% (240x160, the same points and size as the scan's screenshots, which the interfaces show next to the
    /// original's). A picture that cannot be made or decoded means the file is incomplete or broken. One more picture is
    /// taken from the last seconds - a file that was cut off can still say it has its full length - but not kept.
    /// </summary>
    public sealed class EncodeChecker
    {
        private readonly IEncodeRunner runner;
        private readonly ImageHandler images = new();

        public EncodeChecker(IEncodeRunner runner) => this.runner = runner;

        /// <summary>The picture of the checked file kept for the interfaces (n = 1..3).</summary>
        public static string ShotPath(string folder, string itemId, int n) => Path.Combine(folder, $"{itemId}_{n}.png");

        public static void DeleteShots(string folder, string itemId)
        {
            for (int n = 1; n <= 3; n++)
            {
                try { File.Delete(ShotPath(folder, itemId, n)); } catch { /* gone or in use: harmless */ }
            }
        }

        /// <param name="ffmpeg">Path of ffmpeg; ffprobe is next to it</param>
        /// <param name="expectedSeconds">The length of the original, from the database</param>
        public async Task<EncodeCheckResult> CheckAsync(string ffmpeg, string file, int expectedSeconds, string folder, string itemId, CancellationToken ct)
        {
            Directory.CreateDirectory(folder);
            DeleteShots(folder, itemId);

            // 1. Length and a video stream
            string ffprobe = Subtitles.FfprobePath(ffmpeg);
            double? seconds = null;
            bool video = false;
            string lastError = "";
            int exit = await runner.RunAsync(ffprobe, new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "default=noprint_wrappers=1", file },
                line =>
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) return;
                    string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
                    if (key == "duration" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) seconds = d;
                    if (key == "codec_type" && value == "video") video = true;
                },
                line => { if (!string.IsNullOrWhiteSpace(line)) lastError = line.Trim(); }, ct);
            if (exit != 0 || !video) return Bad($"the file cannot be read ({(lastError.Length > 0 ? lastError : "no video found in it")})");
            if (seconds is not double actual || actual <= 0) return Bad("the file has no length, so it is incomplete");

            double tolerance = Math.Max(2, expectedSeconds * 0.01);
            if (expectedSeconds > 0 && Math.Abs(actual - expectedSeconds) > tolerance)
                return Bad($"it is {Fmt.Duration((int)Math.Round(actual))} long, the original is {Fmt.Duration(expectedSeconds)}");

            // 2. The pictures: the scan's three points (from the original's length), then the last seconds
            int length = expectedSeconds > 0 ? expectedSeconds : (int)Math.Round(actual);
            var points = new[] { length / 4, length / 4 * 2, length / 4 * 3 };
            var tasks = points.Select((point, i) => TakePicture(ffmpeg, file, point, ShotPath(folder, itemId, i + 1), ct)).ToList();
            string endPicture = Path.Combine(folder, itemId + "_end.png");
            tasks.Add(TakePicture(ffmpeg, file, Math.Max((int)actual - 1, points[2]), endPicture, ct));
            string?[] problems = await Task.WhenAll(tasks);
            TryDelete(endPicture);

            string[] where = { "25%", "50%", "75%", "the end" };
            int made = Enumerable.Range(1, 3).Count(n => File.Exists(ShotPath(folder, itemId, n)));
            for (int i = 0; i < problems.Length; i++)
                if (problems[i] != null) return Bad($"the picture at {where[i]} could not be made ({problems[i]})", made);

            return new EncodeCheckResult(true, $"Same length ({Fmt.Duration((int)Math.Round(actual))}), pictures at 25%, 50%, 75% and the end read", "", 3);
        }

        private static EncodeCheckResult Bad(string problem, int pictures = 0) => new(false, "", problem, pictures);

        // One picture like the scan's (the same frame, see Picture), 240x160. Null when it was made and could be decoded.
        private async Task<string?> TakePicture(string ffmpeg, string file, int second, string output, CancellationToken ct)
        {
            string lastError = "", empty = "";
            int exit = await runner.RunAsync(ffmpeg, Picture.Arguments(file, second, output, 240, 160),
                _ => { },
                line =>
                {
                    string text = line.Trim();
                    if (text.Length == 0 || text.StartsWith("frame=") || text.StartsWith("size=")) return;   // progress, not a reason
                    if (text.Contains("Output file is empty")) empty = text;
                    lastError = text;
                }, ct);
            if (exit != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
                return empty.Length > 0 ? "the video ends before that point" : lastError.Length > 0 ? lastError : "no picture came out";
            try { images.imageChecksum(output); }   // the scan's own check that the picture decodes
            catch (Exception e) { return "the picture is not readable: " + e.Message; }
            return null;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}

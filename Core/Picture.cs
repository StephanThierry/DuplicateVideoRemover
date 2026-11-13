using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace deepduplicates
{
    /// <summary>
    /// How a picture is taken from a video: the scan's pictures (compared to find duplicates) and the check of a re-encoded file
    /// (shown next to the original's) are taken the same way, so the same frame comes out of both files.
    ///
    /// The picture is the first frame that STARTS at or after the second. Plain "-ss second" gives the frame that is still on screen
    /// at that second, which depends on how long each frame lasts: a re-encode keeps the frames' times but not always their lengths
    /// (variable frame rate, a video that starts a moment after the sound), so that gave a frame or two apart in the two files - and
    /// pictures that did not match. FFmpeg starts decoding a second earlier and the select filter picks the frame; a frame that is a
    /// hair (a rounding of the time base) before the second still counts as on it.
    /// </summary>
    public static class Picture
    {
        private const double LeadSeconds = 1;
        private const double Tolerance = 0.002;

        public static List<string> Arguments(string video, int second, string output, int width, int height)
        {
            double start = Math.Max(second - LeadSeconds, 0);
            string from = (second - start - Tolerance).ToString("0.###", CultureInfo.InvariantCulture);
            var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-y" };
            if (start > 0) args.AddRange(new[] { "-ss", start.ToString("0.###", CultureInfo.InvariantCulture) });
            // One picture, written as it is: no frames repeated to fill the time before it (that made FFmpeg count up to
            // hundreds of thousands of copies on some files), and no "not an image sequence" warning
            args.AddRange(new[] { "-i", video, "-map", "0:v:0", "-vf", $"select=gte(t\\,{from}),scale={width}:{height}", "-frames:v", "1",
                "-fps_mode", "passthrough", "-update", "1", output });
            return args;
        }

        /// <summary>
        /// Takes one picture with FFmpeg. Judged like FFmpeg itself does: it finished without an error and the picture is there.
        /// (Xabe, used before, threw as soon as FFmpeg mentioned damaged data - also when the picture came out fine -
        /// so whole videos with a few broken frames were listed as broken.) Throws <see cref="PictureFailedException"/>.
        /// </summary>
        public static async Task TakeAsync(string ffmpeg, string video, int second, string output, int width, int height)
        {
            var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in Arguments(video, second, output, width, height)) info.ArgumentList.Add(a);
            using var process = Process.Start(info) ?? throw new PictureFailedException("FFmpeg could not be started", false);
            var stdout = process.StandardOutput.ReadToEndAsync();
            string errors = await process.StandardError.ReadToEndAsync();
            await stdout;
            await process.WaitForExitAsync();
            bool made = File.Exists(output) && new FileInfo(output).Length > 0;
            if (process.ExitCode == 0 && made) return;
            string last = errors.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("frame=")) ?? "";
            bool unsupported = errors.Contains("Unknown decoder") || errors.Contains("No decoder") || (errors.Contains("Decoder (codec") && errors.Contains("not found"));
            throw new PictureFailedException(process.ExitCode == 0 ? "no picture came out (the video ends before that point?)" : last, unsupported);
        }

    }

    /// <summary>A picture could not be taken. <see cref="Unsupported"/>: FFmpeg has no decoder for the video.</summary>
    public sealed class PictureFailedException : Exception
    {
        public bool Unsupported { get; }
        public PictureFailedException(string message, bool unsupported) : base(message) => Unsupported = unsupported;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
            args.AddRange(new[] { "-i", video, "-map", "0:v:0", "-vf", $"select=gte(t\\,{from}),scale={width}:{height}", "-frames:v", "1", output });
            return args;
        }

        /// <summary>The same as one command line (for Xabe's Start(parameters)); paths are quoted.</summary>
        public static string CommandLine(IEnumerable<string> args) =>
            string.Join(" ", args.Select(a => a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\""));
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace deepduplicates
{
    /// <summary>
    /// Carries the subtitles of a video over to its re-encoded file. MKV takes nearly every kind as it is (only MP4's own
    /// mov_text has to become SRT). MP4 only takes mov_text: text subtitles are converted, picture subtitles (Blu-ray PGS,
    /// DVD, DVB) cannot be stored in it and are left out - mapping them would make FFmpeg fail.
    /// </summary>
    public static class Subtitles
    {
        // Kinds MKV stores as they are
        private static readonly HashSet<string> MkvCopy = new(StringComparer.OrdinalIgnoreCase)
            { "subrip", "srt", "ass", "ssa", "webvtt", "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle" };

        // Text kinds that can be converted (to SRT for MKV, to mov_text for MP4)
        private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase)
            { "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text", "microdvd", "sami", "realtext", "subviewer", "subviewer1",
              "jacosub", "mpl2", "pjs", "vplayer", "stl" };

        private static readonly HashSet<string> Mp4Family = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov" };

        /// <summary>
        /// The FFmpeg arguments that map and encode the subtitles. <paramref name="codecs"/> are the input's subtitle
        /// streams in order (from <see cref="ProbeAsync"/>); null when unknown (the script), then only what is safe for
        /// any file of that type is done.
        /// </summary>
        public static List<string> Arguments(string input, string container, IReadOnlyList<string>? codecs)
        {
            var args = new List<string>();
            bool fromMp4 = Mp4Family.Contains(Path.GetExtension(input));
            if (codecs == null)
            {
                // MP4-family files only hold mov_text: fine for both containers. Other files may hold picture
                // subtitles: MKV keeps them as they are, MP4 cannot take them so none are mapped.
                if (container == "mkv") args.AddRange(new[] { "-map", "0:s?", "-c:s", fromMp4 ? "srt" : "copy" });
                else if (fromMp4) args.AddRange(new[] { "-map", "0:s?", "-c:s", "mov_text" });
                return args;
            }

            var encoders = new List<string>();
            for (int i = 0; i < codecs.Count; i++)
            {
                string c = codecs[i];
                string? encoder = container == "mkv"
                    ? MkvCopy.Contains(c) ? "copy" : Text.Contains(c) ? "srt" : null
                    : Text.Contains(c) ? "mov_text" : null;
                if (encoder == null) continue;
                args.AddRange(new[] { "-map", "0:s:" + i });
                encoders.Add(encoder);
            }
            for (int k = 0; k < encoders.Count; k++) args.AddRange(new[] { "-c:s:" + k, encoders[k] });
            return args;
        }

        /// <summary>The input's subtitle kinds in stream order, or null when ffprobe could not read the file.</summary>
        public static async Task<IReadOnlyList<string>?> ProbeAsync(IEncodeRunner runner, string ffmpeg, string file, CancellationToken ct)
        {
            var codecs = new List<string>();
            int exit = await runner.RunAsync(FfprobePath(ffmpeg), new[] { "-v", "error", "-select_streams", "s", "-show_entries", "stream=codec_name",
                    "-of", "default=noprint_wrappers=1", file },
                line =>
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && line[..eq].Trim() == "codec_name") codecs.Add(line[(eq + 1)..].Trim());
                },
                _ => { }, ct);
            return exit == 0 ? codecs : null;
        }

        /// <summary>ffprobe sits next to ffmpeg.</summary>
        public static string FfprobePath(string ffmpeg) =>
            Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe"));
    }
}

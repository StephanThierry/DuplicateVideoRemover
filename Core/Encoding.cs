using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace deepduplicates
{
    public sealed record CodecOption(string Id, string Label, string Help, string Encoder, int MinCrf, int MaxCrf, int RecommendedCrf);
    public sealed record ChoiceOption(string Id, string Label);
    public sealed record ResolutionOption(int Height, string Label);

    /// <summary>
    /// The choices the interfaces offer for re-encoding - only the common, well-supported ones.
    /// </summary>
    public static class EncodeOptions
    {
        // CRF: lower = better quality and bigger files. The recommended values give a result that is hard to
        // tell from the original at normal viewing (x265 needs a higher number than x264 for the same quality).
        public static readonly CodecOption[] Codecs =
        {
            new("x265", "H.265 (x265)", "About half the file size of H.264 at the same quality. Plays on newer TVs, phones and computers.", "libx265", 16, 34, 22),
            new("x264", "H.264 (x264)", "Plays on almost everything, also older devices. Bigger files than H.265.", "libx264", 14, 32, 20),
        };

        public static readonly ResolutionOption[] Resolutions =
        {
            new(720, "720p (HD)"),
            new(1080, "1080p (Full HD)"),
            new(1440, "1440p (QHD)"),
        };
        public const int DefaultResolution = 1080;

        public static readonly ChoiceOption[] Presets =
        {
            new("veryfast", "Very fast - biggest files"),
            new("faster", "Faster"),
            new("fast", "Fast"),
            new("medium", "Medium - recommended"),
            new("slow", "Slow - smaller files"),
            new("slower", "Slower - smallest files, takes much longer"),
        };
        public const string DefaultPreset = "medium";

        public static readonly ChoiceOption[] AudioChoices =
        {
            new("copy", "Keep the original sound - recommended"),
            new("aac", "Convert to AAC 160 kbps"),
        };

        public static readonly ChoiceOption[] Containers =
        {
            new("mp4", "MP4 - recommended"),
            new("mkv", "MKV"),
        };

        public static CodecOption Codec(string id) => Codecs.FirstOrDefault(c => c.Id == id) ?? Codecs[0];

        public static int NearestResolution(int height) =>
            Resolutions.OrderBy(r => Math.Abs(r.Height - height)).First().Height;

        /// <summary>A short description of what a quality value gives, for the slider label.</summary>
        public static string QualityLabel(string codecId, int crf)
        {
            int d = crf - Codec(codecId).RecommendedCrf;
            if (d <= -5) return "Near lossless - very big files";
            if (d <= -2) return "Excellent - bigger files";
            if (d <= -1) return "Very good, slightly bigger files";
            if (d == 0) return "Recommended - looks like the original";
            if (d <= 2) return "Good - smaller files";
            if (d <= 5) return "Fair - fine detail is lost";
            return "Low - clearly worse than the original";
        }
    }

    /// <summary>A video that could be re-encoded.</summary>
    public sealed class EncodeCandidate
    {
        public int Id { get; init; }
        public string Path { get; init; } = "";
        public long SizeBytes { get; init; }
        public int DurationSeconds { get; init; }
        /// <summary>Average bitrate from size and length, in kbit/s (what the list is sorted by).</summary>
        public double AverageKbps => DurationSeconds > 0 ? SizeBytes * 8.0 / DurationSeconds / 1000.0 : 0;
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
        public string FileName => System.IO.Path.GetFileName(Path);
    }

    /// <summary>Finds videos worth re-encoding and writes the encoding script.</summary>
    public static class EncodePlanner
    {
        /// <summary>
        /// Videos that can be re-encoded, highest average bitrate first: not marked as duplicate or broken,
        /// not of a skipped file type, longer than the minimum, and not a file this script made before.
        /// </summary>
        public static List<EncodeCandidate> Candidates(IEnumerable<VideoInfo> analyzed, EncodeSettings e)
        {
            var skip = new HashSet<string>(e.skipExtensions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            string suffix = e.EffectiveSuffix;
            return analyzed
                .Where(v => v.remove != true && !string.IsNullOrEmpty(v.path) && (v.duration ?? 0) > e.minDurationSeconds && (v.fileSize ?? 0) > 0)
                .Where(v => !skip.Contains(Path.GetExtension(v.path)))
                .Where(v => !Path.GetFileNameWithoutExtension(v.path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Select(v => new EncodeCandidate { Id = v.id, Path = v.path, SizeBytes = v.fileSize ?? 0, DurationSeconds = v.duration ?? 0 })
                .OrderByDescending(c => c.AverageKbps)
                .ToList();
        }

        /// <summary>Where the new file goes: next to the original, with the suffix and the container's extension.</summary>
        public static string OutputPath(string input, EncodeSettings e)
        {
            string dir = Path.GetDirectoryName(input) ?? "";
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(input) + e.EffectiveSuffix + "." + e.container);
        }

        /// <summary>The FFmpeg arguments for one video.</summary>
        public static List<string> Arguments(string input, EncodeSettings e)
        {
            var codec = EncodeOptions.Codec(e.codec);
            // Scale to the target height keeping the aspect ratio (width divisible by 2); never enlarge.
            // The comma inside min() is escaped for FFmpeg's filter syntax.
            string scale = $"scale=-2:min({e.resolution}\\,ih)";
            string filter = string.IsNullOrWhiteSpace(e.extraFilter) ? scale : e.extraFilter + "," + scale;

            var args = new List<string> { e.neverOverwrite ? "-n" : "-y", "-i", input, "-map", "0:v:0", "-map", "0:a?",
                "-c:v", codec.Encoder, "-crf", e.crf.ToString(), "-preset", e.preset, "-vf", filter, "-pix_fmt", "yuv420p" };
            if (codec.Id == "x265" && e.container == "mp4" && e.appleCompatible) args.AddRange(new[] { "-tag:v", "hvc1" });
            if (e.audio == "aac") args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
            else args.AddRange(new[] { "-c:a", "copy" });
            if (e.container == "mp4") args.AddRange(new[] { "-movflags", "+faststart" });
            args.Add(OutputPath(input, e));
            return args;
        }

        /// <summary>The command as shown in the settings preview.</summary>
        public static string ExampleCommand(EncodeSettings e, string exampleInput) =>
            "ffmpeg " + string.Join(" ", Arguments(exampleInput, e).Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));

        /// <summary>
        /// Writes [name].ps1 (Windows) or [name].sh (Linux) to the output folder with one FFmpeg command per video,
        /// in the given order. Returns the script's path. The script is run by the user; nothing is encoded here.
        /// </summary>
        public static string WriteScript(string outputFolder, string name, IEnumerable<EncodeCandidate> videos, EncodeSettings e)
        {
            // Scripts start in their own folder (output), so ../ffmpeg is the program's FFmpeg folder
            string ffmpeg = ScriptFile.IsWindows ? "..\\ffmpeg\\ffmpeg.exe" : "../ffmpeg/ffmpeg";
            var codec = EncodeOptions.Codec(e.codec);
            var list = videos.ToList();
            using var script = new ScriptFile(outputFolder, name);
            script.Comment($"Re-encodes {list.Count} videos to {codec.Label}, {e.resolution}p, quality {e.crf} ({EncodeOptions.QualityLabel(e.codec, e.crf ?? 0)}), preset {e.preset}.");
            script.Comment("Highest average bitrate first. The originals are kept; check the new files before deleting anything.");
            script.Comment($"Made by Duplicate Video Remover on {DateTime.Now:yyyy-MM-dd HH:mm}.");
            script.Line();
            foreach (var v in list)
            {
                script.Comment($"{v.AverageKbps:N0} kbit/s, {Fmt.Size(v.SizeBytes)}, {Fmt.Duration(v.DurationSeconds)}");
                script.Run(ffmpeg, Arguments(v.Path, e).ToArray());
                script.Line();
            }
            return script.FilePath;
        }
    }
}

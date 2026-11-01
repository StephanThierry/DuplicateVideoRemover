using System;
using System.Linq;

namespace deepduplicates
{
    public class Settings
    {
        public static Settings CreateDefault()
        {
            var s = new Settings
            {
                contentFolders = new[] { OperatingSystem.IsWindows() ? @"c:\MyVideos_changeThis" : "/path/to/videos_changeThis" },
                excludePaths = new string[0],
                priorityFolders = new string[0],
                retainStructure = new string[0],
                retainIncomingFilenames = new string[0],
                switchPriority = new[] { new switchPrioritySet { down = "x264", up = "x265", triggerBelowPctSizeDiff = 15 } },
                logInterval = 100,
                minVideoLength = 3,
                matchSettings = matchSettingsSet.Recommended()
            };
            return s;
        }

        /// <summary>Fills in missing values, so code and interfaces never see null lists.</summary>
        public void Normalize()
        {
            contentFolders ??= new string[0];
            excludePaths ??= new string[0];
            priorityFolders ??= new string[0];
            retainStructure ??= new string[0];
            retainIncomingFilenames ??= new string[0];
            switchPriority ??= new switchPrioritySet[0];
            matchSettings ??= matchSettingsSet.Recommended();
            encodeSettings?.Normalize();
            if (logInterval <= 0) logInterval = 100;
        }

        /// <summary>The encoding settings, or the defaults when settings.json has none.</summary>
        public EncodeSettings EncodeOrDefault() => encodeSettings?.Clone() ?? EncodeSettings.CreateDefault();

        public Settings Clone() =>
            System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(this));

        public string[] contentFolders { get; set; }
        public string[] excludePaths { get; set; }
        public string[] priorityFolders { get; set; }
        public string[] retainStructure { get; set; }
        public string[] retainIncomingFilenames { get; set; }
        public switchPrioritySet[] switchPriority { get; set; }
        public int logInterval { get; set; }
        public int minVideoLength { get; set; }
        public int minVideoSizeKb { get; set; }
        public EncodeSettings encodeSettings { get; set; }

        public matchSettingsSet matchSettings { get; set; }

        /// <summary>
        /// This matchSettingsSet is only used if the files are  in the same folder - this is to take into account differences introduced by recompression
        /// </summary>
        public matchSettingsSet matchSettings_sameFolder { get; set; }
    }

    public class switchPrioritySet
    {
        public string up { get; set; }
        public string down { get; set; }
        public int triggerBelowPctSizeDiff { get; set; }

    }

    /// <summary>
    /// Settings for the re-encoding script. Every value has a sensible default (see CreateDefault), and the
    /// allowed values are listed in EncodeOptions. Older settings files with scale / compressionLevelcrf /
    /// compressionPreset are read and converted by Normalize().
    /// </summary>
    public class EncodeSettings
    {
        /// <summary>"x265" (H.265) or "x264" (H.264).</summary>
        public string codec { get; set; }
        /// <summary>Target height: 720, 1080 or 1440. Smaller videos keep their size.</summary>
        public int resolution { get; set; }
        /// <summary>Quality (CRF): lower is better quality and bigger files. Null = the codec's recommended value.</summary>
        public int? crf { get; set; }
        /// <summary>Encoder speed preset (veryfast ... slower).</summary>
        public string preset { get; set; }
        /// <summary>"copy" keeps the original sound, "aac" converts it to AAC 160 kbps.</summary>
        public string audio { get; set; }
        /// <summary>"mp4" or "mkv".</summary>
        public string container { get; set; }
        /// <summary>Added to the file name of the new file. Empty = automatic, e.g. "_720p_x265".</summary>
        public string fileSuffix { get; set; }
        /// <summary>H.265 in MP4: tag the video so Apple devices play it.</summary>
        public bool appleCompatible { get; set; } = true;
        /// <summary>Never overwrite a file that already exists.</summary>
        public bool neverOverwrite { get; set; } = true;
        /// <summary>File types left out (their length is often read wrong).</summary>
        public string[] skipExtensions { get; set; }
        /// <summary>Videos this short or shorter are left out.</summary>
        public int minDurationSeconds { get; set; }
        /// <summary>Advanced: extra FFmpeg video filter, applied before scaling (e.g. "yadif" to deinterlace).</summary>
        public string extraFilter { get; set; }

        // Older settings files (read only; converted by Normalize)
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string scale { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string compressionLevelcrf { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string compressionPreset { get; set; }

        public static EncodeSettings CreateDefault()
        {
            var e = new EncodeSettings();
            e.Normalize();
            return e;
        }

        public EncodeSettings Clone()
        {
            var c = (EncodeSettings)MemberwiseClone();
            c.skipExtensions = (string[])skipExtensions?.Clone();
            return c;
        }

        /// <summary>Converts old values and replaces missing or unknown values with the defaults.</summary>
        public void Normalize()
        {
            // Old format: scale "1280:720", compressionLevelcrf "24", compressionPreset "superfast"
            if (resolution == 0 && !string.IsNullOrWhiteSpace(scale))
            {
                var parts = scale.Split(':', 'x');
                if (parts.Length == 2 && int.TryParse(parts[1], out int h) && h > 0) resolution = EncodeOptions.NearestResolution(h);
            }
            if (crf == null && int.TryParse(compressionLevelcrf, out int oldCrf)) crf = oldCrf;
            if (preset == null && !string.IsNullOrWhiteSpace(compressionPreset)) preset = compressionPreset.Trim().ToLowerInvariant();
            scale = compressionLevelcrf = compressionPreset = null;

            var codecInfo = EncodeOptions.Codec(codec);
            codec = codecInfo.Id;
            if (!EncodeOptions.Resolutions.Any(r => r.Height == resolution)) resolution = EncodeOptions.DefaultResolution;
            crf = Math.Clamp(crf ?? codecInfo.RecommendedCrf, codecInfo.MinCrf, codecInfo.MaxCrf);
            preset = EncodeOptions.Presets.Any(p => p.Id == preset) ? preset : EncodeOptions.DefaultPreset;
            audio = EncodeOptions.AudioChoices.Any(a => a.Id == audio) ? audio : "copy";
            container = EncodeOptions.Containers.Any(c => c.Id == container) ? container : "mp4";
            fileSuffix = (fileSuffix ?? "").Trim();
            skipExtensions ??= new[] { ".mpg", ".mpeg", ".wmv" };
            if (minDurationSeconds <= 0) minDurationSeconds = 2;
            extraFilter = (extraFilter ?? "").Trim().TrimEnd(',');
        }

        /// <summary>What is added to the file name: the chosen ending, or "_720p_x265" style.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string EffectiveSuffix => string.IsNullOrWhiteSpace(fileSuffix) ? $"_{resolution}p_{codec}" : fileSuffix;
    }

    public class matchSettingsSet
    {
        public static matchSettingsSet Recommended() =>
            new matchSettingsSet { colorTolerance = 35, shapeMatch = 280, faultTolerance = 0, triggerOnNameMatch = false };

        public matchSettingsSet Clone() => (matchSettingsSet)MemberwiseClone();

        /// <summary>
        /// Tolerance for color accuracy. Lower value means "more accurate predictions".
        /// </summary>
        /// <value>Recommended value: 35</value>
        public int colorTolerance { get; set; }
        
        /// <summary>
        /// Threshhold for shape accuracy. How well the shape of the image matches on a scale from 0-400. Higher value means "more accurate predictions".
        /// </summary>
        /// <value>Recommended value: 280</value>
        public int shapeMatch { get; set; }
        /// <summary>
        /// faultTolerance. 0 (highest = none of the 6 indicators may fall outside bounds) - 3 (lowest = 3 of 6 indicators may fall outside bounds and still produce match). 
        /// Values higher than 1 will produce many false positives, unless colorTolerance and shapeMatch values are very high.
        /// </summary>
        /// <value>Recommended value: 0</value>
        public int faultTolerance { get; set; }

        public bool triggerOnNameMatch { get; set; }

    }
}
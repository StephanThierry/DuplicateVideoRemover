using System;

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
            if (logInterval <= 0) logInterval = 100;
        }

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

    public class EncodeSettings
    {
        public string scale  { get; set; }
        public string compressionLevelcrf { get; set; }
        public string compressionPreset { get; set; }
        public string extraFilter { get; set; }
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
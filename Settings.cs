namespace deepduplicates
{
    public class Settings
    {
        public string[] contentFolders { get; set; }
        public string[] excludePaths { get; set; }
        public string[] priorityFolders { get; set; }
        public string[] retainStructure { get; set; }
        public string[] retainIncomingFilenames { get; set; }
        public switchPrioritySet[] switchPriority { get; set; }
        public int logInterval { get; set; }
        public int minVideoLength { get; set; }
        public int minVideoSizeKb { get; set; }

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

    public class matchSettingsSet
    {
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
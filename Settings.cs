namespace deepduplicates
{
    public class Settings
    {
        public string[] contentFolders { get; set; }
        public string[] excludePaths { get; set; }
        public string[] priorityFolders { get; set; }
        public switchPrioritySet[] switchPriority { get; set; }
        public int logInterval { get; set; }
        public int minVideoLength { get; set; }

        public matchSettingsSet matchSettings { get; set; }
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
        /// </summary>
        /// <value>Recommended value: 0</value>
        public int faultTolerance { get; set; }

    }
}
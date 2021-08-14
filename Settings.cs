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
    }

    public class switchPrioritySet
    {
        public string up { get; set; }
        public string down { get; set; }
    }
}
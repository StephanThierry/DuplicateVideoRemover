namespace deepduplicates
{
    public class Settings
    {
        public string[] contentFolders {get; set;}
        public string[] excludeFolders {get; set;}
        public string[] priorityFolders {get; set;}
        public int logInterval { get; set; }

        public int minVideoLength { get; set; }
    }
}
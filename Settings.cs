namespace deepduplicates
{
    public class Settings
    {
        public string[] contentFolders {get; set;}
        public string ffmpegFolder {get; set;}
        public int logInterval { get; set; }

        public int minVideoLength { get; set; }
    }
}
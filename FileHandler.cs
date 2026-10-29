using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xabe.FFmpeg.Enums;

namespace deepduplicates
{
    public class FileHandler
    {
        public string screenshotFolder { get; set; }
        public string outputFolder { get; set; }
        public IEnumerable<string> allFiles { get; set; }
        public int allFilesCount { get; set; }

        private static string appFolder = Directory.GetCurrentDirectory();
        public Settings settings = null;

        public bool firstRun = false;

        private static string lameJSONBeautifier(string json)
        {
            string[] postChars = { "{", "," };
            string[] preChars = { "}" };
            foreach (string c in postChars) json = json.Replace(c, c + Environment.NewLine);
            foreach (string c in preChars) json = json.Replace(c, Environment.NewLine + c);

            return (json);
        }

        private static void InitConfig(FileHandler instance)
        {
            string settingsPath = Path.Combine(appFolder, "settings.json");
            string settings_json = "";
            try
            {
                settings_json = System.IO.File.ReadAllText(settingsPath);
            }
            catch
            {
                // Presumably settings.json does not exist - set default values and save
                Console.WriteLine("Creating 'settings.json' - edit settings and run again.");
                instance.settings = new Settings();
                instance.settings.contentFolders = new string[0];
                instance.settings.retainStructure = new string[0];
                instance.settings.priorityFolders = new string[0];
                instance.settings.excludePaths = new string[0];
                instance.settings.retainIncomingFilenames = new string[0];
                instance.settings.minVideoLength = 3;
                instance.settings.contentFolders = new string[] { @"c:\MyVideos_changeThis" };
                instance.settings.switchPriority = new switchPrioritySet[1];
                switchPrioritySet spExample = new switchPrioritySet();
                spExample.down = "x264";
                spExample.up = "x265";
                spExample.triggerBelowPctSizeDiff = 15;
                instance.settings.switchPriority[0] = spExample;
                
                instance.settings.logInterval = 100;
                instance.settings.matchSettings = new matchSettingsSet();
                instance.settings.matchSettings.colorTolerance = 35;
                instance.settings.matchSettings.shapeMatch = 280;
                instance.settings.matchSettings.faultTolerance = 0;

                File.WriteAllText(settingsPath, lameJSONBeautifier(JsonSerializer.Serialize(instance.settings)));
                instance.firstRun = true;
            }

            if (instance.settings == null) instance.settings = JsonSerializer.Deserialize<Settings>(settings_json);

            // Paths in settings.json may be written in Windows or Linux form - convert them to the current OS
            instance.settings.contentFolders = MapPaths(instance.settings.contentFolders);
            instance.settings.excludePaths = MapPaths(instance.settings.excludePaths);
            instance.settings.priorityFolders = MapPaths(instance.settings.priorityFolders);
            instance.settings.retainStructure = MapPaths(instance.settings.retainStructure);
            instance.settings.retainIncomingFilenames = MapPaths(instance.settings.retainIncomingFilenames);
        }

        private static string[] MapPaths(string[] paths)
        {
            return (paths ?? new string[0]).Select(PathMapper.ToLocal).ToArray();
        }

        public string screenshotPath(VideoInfo item, int prefix)
        {
            return (Path.Combine(this.screenshotFolder, item.id + "_" + prefix + FileExtensions.Png));
        }

        private FileHandler()
        {
            // Block non-async object creation
        }

        public static async Task<FileHandler> CreateInstance()
        {
            FileHandler instance = new FileHandler();

            InitConfig(instance);

            string ffmpegFolder = Path.Combine(appFolder, "ffmpeg");
            Directory.CreateDirectory(ffmpegFolder);
            Xabe.FFmpeg.FFmpeg.ExecutablesPath = ffmpegFolder;
            if (!OperatingSystem.IsWindows() && LinkSystemFFmpeg(ffmpegFolder))
            {
                Console.WriteLine("Using FFmpeg installed on the system.");
            }
            else
            {
                // Fails: C:\dev\github.com\Xabe.FFmpeg\Xabe.FFmpeg\Downloader\FFMpegDownloaderBase.cs:line 52
                Console.WriteLine("Checking for latest version of FFmpeg in " + ffmpegFolder);
                await Xabe.FFmpeg.FFmpeg.GetLatestVersion();
                MakeExecutable(Path.Combine(ffmpegFolder, "ffmpeg"));
                MakeExecutable(Path.Combine(ffmpegFolder, "ffprobe"));
                Console.WriteLine("Done.");
            }
            if (instance.firstRun) return (instance);

            instance.screenshotFolder = Path.Combine(appFolder, "_screens");
            Directory.CreateDirectory(instance.screenshotFolder);

            instance.outputFolder = Path.Combine(appFolder, "output");
            Directory.CreateDirectory(instance.outputFolder);

            instance.allFiles = null;
            foreach (string contentFolder in instance.settings.contentFolders)
            {
                try{
                Console.WriteLine("Indexing all files in: " + contentFolder + "...");
                    IEnumerable<string> files = instance.GetFiles(contentFolder, new[] { ".avi", ".divx", ".mp4", ".m4v", ".mov", ".wmv", ".mpg", ".mpeg", ".flv", ".mkv" }, instance.settings.excludePaths);
                if (instance.allFiles == null)
                {
                    instance.allFiles = files;
                }
                else
                {
                    instance.allFiles = instance.allFiles.Concat(files);
                }

                Console.WriteLine("Found a total of " + instance.allFiles.Count() + " video files.");
                } catch {
                    Console.WriteLine("Failed to get " + contentFolder + " - skipping.");
                }
            }
            instance.allFilesCount = instance.allFiles.Count();

            return (instance);
        }

        /// <summary>
        /// Linux/macOS: if ffmpeg and ffprobe are installed (e.g. "apt install ffmpeg"), link them into
        /// the ffmpeg folder instead of downloading. Returns false if they are not found.
        /// </summary>
        private static bool LinkSystemFFmpeg(string ffmpegFolder)
        {
            string ffmpeg = FindOnPath("ffmpeg"), ffprobe = FindOnPath("ffprobe");
            if (ffmpeg == null || ffprobe == null) return false;

            foreach (var (name, target) in new[] { ("ffmpeg", ffmpeg), ("ffprobe", ffprobe) })
            {
                string link = Path.Combine(ffmpegFolder, name);
                var existing = new System.IO.FileInfo(link);
                if (existing.Exists || existing.LinkTarget != null) existing.Delete();
                File.CreateSymbolicLink(link, target);
            }
            // Remove anything else the Xabe lookup could mistake for ffmpeg/ffprobe
            foreach (string file in Directory.GetFiles(ffmpegFolder))
            {
                string name = Path.GetFileName(file);
                if (name != "ffmpeg" && name != "ffprobe") File.Delete(file);
            }
            return true;
        }

        private static string FindOnPath(string executable)
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate = Path.Combine(dir, executable);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static void MakeExecutable(string path)
        {
            if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
            File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        public long GetFileSize(string fullPath)
        {
            try
            {
                using (var stream = File.OpenRead(fullPath))
                {

                    long size = stream.Length;
                    stream.Close();
                    return size;
                }
            }
            catch(Exception e)
            {
                Console.WriteLine("Can't read filesize: " + e.Message + " - " + fullPath);
                return (-1);
            }
        }
        private IEnumerable<string> GetFiles(string path, string[] ext, string[] excludePaths)
        {
            Queue<string> queue = new Queue<string>();
            queue.Enqueue(path);
            while (queue.Count > 0)
            {
                path = queue.Dequeue();
                try
                {
                    foreach (string subDir in Directory.GetDirectories(path))
                    {
                        queue.Enqueue(subDir);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore this error
                }
                catch //(Exception ex)
                {
                    //Console.Error.WriteLine(ex); -- ignore
                }
                string[] files = null;
                try
                {
                    files = Directory.GetFiles(path);
                }
                catch (UnauthorizedAccessException)
                {
                    // ignore this error
                }
                catch //(Exception ex)
                {
                    //Console.Error.WriteLine(ex); - ignore
                }
                if (files != null)
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        // Extension match ignores case, so ".MP4" is found too
                        if (ext.Contains(Path.GetExtension(files[i]), StringComparer.OrdinalIgnoreCase) && !excludePaths.Any(x => files[i].Contains(x) || files[i].Equals(x))) yield return files[i];
                    }
                }
            }
        }


        public string batchPath(string path){
            // % needs to be escaped to %% to work in a .bat file
            return path.Replace("%", "%%");
        }

        // Writes delete_all_dupes.bat on Windows, delete_all_dupes.sh on Linux/macOS
        public void generateBatchFile(List<VideoInfo> mediaList)
        {
            bool retainStructure = (settings.retainStructure.Length > 0);
            using (var script = new ScriptFile(this.outputFolder, "delete_all_dupes"))
            {
                foreach (VideoInfo item in mediaList.Where(x => (x.remove ?? false) && !String.IsNullOrEmpty(x.path)).OrderBy(p => p.triggerId))
                {
                    script.Delete(item.path);
                    if (retainStructure && settings.retainStructure.Where(p => item.path.ToLower().StartsWith(p.ToLower())).Any())
                    {
                        VideoInfo org = mediaList.Where(x => x.id == item.triggerId).FirstOrDefault();
                        if (org == null) continue;
                        script.Move(org.path, item.path);
                    }
                }
            }
        }

        // Writes encoding-template.bat on Windows, encoding-template.sh on Linux/macOS
        public void generateEncoding(List<VideoInfo> mediaList, EncodeSettings encodeSettings)
        {   
            // TODO: Switch to using MediaInfo
            // dotnet add package MediaInfo.Wrapper.Core 
            if (encodeSettings == null) return;

            string ffmpegCommand = ScriptFile.IsWindows ? "..\\ffmpeg\\ffmpeg" : "../ffmpeg/ffmpeg";

            // The Linux script starts by changing to its own folder, so ../ffmpeg works from anywhere
            using (var script = new ScriptFile(this.outputFolder, "encoding-template", changeToScriptFolder: !ScriptFile.IsWindows))
            {
                script.Comment("    This encoding template is a suggestion with highest bitrates at the top");
                script.Comment("    NOTE! Many files will NOT be valid to encode, the bitrate may be due to faulty 'duration'-mesaurements");
                script.Comment("    ------------------------------------------------------------------------------------------------------");
                script.Line();

                if (!String.IsNullOrEmpty(encodeSettings.extraFilter) && !encodeSettings.extraFilter.EndsWith(",")) encodeSettings.extraFilter +=",";

                // mpg/wmv have unreliable duration and are usually low bitrate
                foreach (VideoInfo item in mediaList.Where(x => x.duration != null && x.duration > 1
                        && !x.path.ToLower().EndsWith(".mpg")
                        && !x.path.ToLower().EndsWith(".mpeg")
                        && !x.path.ToLower().EndsWith(".wmv")).OrderByDescending(p => p.fileSize / p.duration))
                {
                    double bitrateKbps = Math.Round(((item.bitrate ?? 1000) / 1000));
                    string modifiedPath = item.path.Substring(0, item.path.LastIndexOf(".")) + "_720p.mp4"; // output ext is always mp4 

                    script.Comment($" Total bitrate: {bitrateKbps} kbps");
                    // -c:v libx265         use h.265 encoding
                    // -vtag hvc1           set the video container to hvc1
                    // -vf scale=1280:720   scale video to 720p, 
                    // -crf XX              set video conpression (Constant Rate Factor). 0-16=lossless, 20=Very good, 24=Good, 28-51=degraded 
                    // crf values explained here: https://goughlui.com/2016/08/27/video-compression-testing-x264-vs-x265-crf-in-handbrake-0-10-5/
                    // This encoding is CPU-only as hardware accelerated encoders do not support CRF
                    // -preset superfast    Only at the "slow"-preset is there a quality gain over "superfast"
                    //  preset values are explained here: https://www.youtube.com/watch?v=bzgFL-gNF9g
                    script.Line($"{ffmpegCommand} -n -i {ScriptFile.Quote(item.path)} -c:v libx265 -vtag hvc1 -vf {encodeSettings.extraFilter}scale={encodeSettings.scale} -crf {encodeSettings.compressionLevelcrf} -preset {encodeSettings.compressionPreset} -c:a copy {ScriptFile.Quote(modifiedPath)}");
                    script.Line();
                }
            }
        }

        public void deleteScreenshots(VideoInfo vi){
            for(int i=1; i<=3; i++){
                string path = screenshotPath(vi, i);
                Console.WriteLine("DELETE: " + path);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        public void generateReport(List<VideoInfo> mediaList)
        {
            new HtmlReport(this).WriteDeleteReport(mediaList, Path.Combine(this.outputFolder, "report.html"));

            // Draft of renames for originals in "retainIncomingFilenames" folders (edit before running)
            bool retainIncomingFilenames = (settings.retainIncomingFilenames.Length > 0);
            if (!retainIncomingFilenames) return;

            var renames = new List<(VideoInfo dupe, VideoInfo org)>();
            foreach (VideoInfo item in mediaList.Where(x => (x.remove ?? false)).OrderBy(p => p.triggerId))
            {
                VideoInfo org = mediaList.Where(x => x.id == item.triggerId).FirstOrDefault();
                if (org != null && settings.retainIncomingFilenames.Where(p => org.path.ToLower().StartsWith(p.ToLower())).Any())
                    renames.Add((item, org));
            }
            if (renames.Count == 0) return;

            using (var script = new ScriptFile(this.outputFolder, "rename-draft"))
            {
                foreach (var (dupe, org) in renames)
                {
                    script.Comment("** " + dupe.path + " ** ");
                    script.Rename(org.path, org.path);
                    script.Line();
                }
            }
        }

        public void generateIndexReport(List<VideoInfo> mediaList)
        {
            new HtmlReport(this).WriteIndexReport(mediaList, Path.Combine(this.outputFolder, "indexReport.html"));
        }

    }
}
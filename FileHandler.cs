using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
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

        /// <summary>The folder with settings.json, the database, screenshots and output (the current directory).</summary>
        public static string AppFolder => appFolder;

        public static string SettingsPath => Path.Combine(appFolder, "settings.json");

        /// <summary>
        /// Loads settings.json, or creates it with default values when it doesn't exist (created = true).
        /// Paths are converted to the current OS (see PathMapper).
        /// </summary>
        public static Settings LoadSettings(out bool created)
        {
            created = false;
            Settings settings;
            if (!File.Exists(SettingsPath))
            {
                Console.WriteLine("Creating 'settings.json' - edit settings and run again.");
                settings = Settings.CreateDefault();
                File.WriteAllText(SettingsPath, lameJSONBeautifier(JsonSerializer.Serialize(settings)));
                created = true;
            }
            else
            {
                settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            }
            settings.Normalize();

            // Paths in settings.json may be written in Windows or Linux form - convert them to the current OS
            settings.contentFolders = MapPaths(settings.contentFolders);
            settings.excludePaths = MapPaths(settings.excludePaths);
            settings.priorityFolders = MapPaths(settings.priorityFolders);
            settings.retainStructure = MapPaths(settings.retainStructure);
            settings.retainIncomingFilenames = MapPaths(settings.retainIncomingFilenames);
            return settings;
        }

        /// <summary>Saves settings.json (used by the web and desktop interfaces).</summary>
        public static void SaveSettings(Settings settings)
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static string[] MapPaths(string[] paths)
        {
            return (paths ?? new string[0]).Select(PathMapper.ToLocal).ToArray();
        }

        public string screenshotPath(VideoInfo item, int prefix)
        {
            return (Path.Combine(this.screenshotFolder, item.id + "_" + prefix + FileExtensions.Png));
        }

        public FileHandler(Settings settings)
        {
            this.settings = settings;
            this.screenshotFolder = DataLocation.ScreenshotFolder;
            Directory.CreateDirectory(this.screenshotFolder);
            this.outputFolder = Path.Combine(appFolder, "output");
            Directory.CreateDirectory(this.outputFolder);
            this.allFiles = new List<string>();
        }

        /// <summary>The original command-line start-up: settings, FFmpeg and the list of video files.</summary>
        public static async Task<FileHandler> CreateInstance()
        {
            Settings settings = LoadSettings(out bool created);
            await EnsureFFmpegAsync();
            FileHandler instance = new FileHandler(settings);
            instance.firstRun = created;
            if (created) return (instance);

            instance.FindFiles(null, CancellationToken.None);
            return (instance);
        }

        /// <summary>Makes sure ffmpeg/ffprobe are available (system install on Linux, else downloaded).</summary>
        public static async Task EnsureFFmpegAsync()
        {
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
        }

        /// <summary>Finds all video files in the content folders. Reports the running count to onCount.</summary>
        public void FindFiles(Action<int> onCount, CancellationToken ct)
        {
            var found = new List<string>();
            foreach (string contentFolder in settings.contentFolders)
            {
                try
                {
                    Console.WriteLine("Indexing all files in: " + contentFolder + "...");
                    foreach (string file in GetFiles(contentFolder, VideoExtensions, settings.excludePaths))
                    {
                        ct.ThrowIfCancellationRequested();
                        found.Add(file);
                        if (found.Count % 200 == 0) onCount?.Invoke(found.Count);
                    }
                    Console.WriteLine("Found a total of " + found.Count + " video files.");
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    Console.WriteLine("Failed to get " + contentFolder + " - skipping.");
                }
            }
            onCount?.Invoke(found.Count);
            this.allFiles = found;
            this.allFilesCount = found.Count;
        }

        public static readonly string[] VideoExtensions = { ".avi", ".divx", ".mp4", ".m4v", ".mov", ".wmv", ".mpg", ".mpeg", ".flv", ".mkv" };

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


        // Writes delete_all_dupes.ps1 on Windows, delete_all_dupes.sh on Linux/macOS
        public void generateBatchFile(List<VideoInfo> mediaList)
        {
            bool retainStructure = (settings.retainStructure.Length > 0);
            using (var script = new ScriptFile(this.outputFolder, "delete_all_dupes"))
            {
                foreach (VideoInfo item in mediaList.Where(x => (x.remove ?? false) && !String.IsNullOrEmpty(x.path)).OrderBy(p => p.triggerId))
                {
                    // The original it matched, commented out: to keep the other copy instead, swap the two lines
                    VideoInfo org = mediaList.Where(x => x.id == item.triggerId).FirstOrDefault();
                    if (org != null) script.DeleteCommentedOut(org.path);
                    script.Delete(item.path);
                    if (retainStructure && settings.retainStructure.Where(p => item.path.ToLower().StartsWith(p.ToLower())).Any())
                    {
                        if (org == null) continue;
                        script.Move(org.path, item.path);
                    }
                }
            }
        }

        // Writes encoding-template.ps1 on Windows, encoding-template.sh on Linux/macOS:
        // every video that is not a duplicate or broken, highest average bitrate first (see EncodePlanner)
        public void generateEncoding(List<VideoInfo> mediaList, EncodeSettings encodeSettings)
        {
            if (encodeSettings == null) return; // only when settings.json has an encodeSettings section
            var e = encodeSettings.Clone();
            e.Normalize();
            EncodePlanner.WriteScript(this.outputFolder, "encoding-template", EncodePlanner.Candidates(mediaList, e), e);
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
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
            // Fails: C:\dev\github.com\Xabe.FFmpeg\Xabe.FFmpeg\Downloader\FFMpegDownloaderBase.cs:line 52
            Console.WriteLine("Checking for latest version of FFmpeg in " + ffmpegFolder);
            await Xabe.FFmpeg.FFmpeg.GetLatestVersion();
            Console.WriteLine("Done.");
            if (instance.firstRun) return (instance);

            instance.screenshotFolder = Path.Combine(appFolder, "_screens");
            Directory.CreateDirectory(instance.screenshotFolder);

            instance.outputFolder = Path.Combine(appFolder, "output");
            Directory.CreateDirectory(instance.outputFolder);

            instance.allFiles = null;
            foreach (string contentFolder in instance.settings.contentFolders)
            {
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
            }
            instance.allFilesCount = instance.allFiles.Count();

            return (instance);
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
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
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
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                }
                if (files != null)
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        if (Array.IndexOf(ext, Path.GetExtension(files[i])) > -1 && !excludePaths.Any(x => files[i].Contains(x) || files[i].Equals(x))) yield return files[i];
                    }
                }
            }
        }


        public string batchPath(string path){
            // % needs to be escaped to %% to work in a .bat file
            return path.Replace("%", "%%");
        }
        public void generateBatchFile(List<VideoInfo> mediaList)
        {
            string filepath = Path.Combine(this.outputFolder, "delete_all_dupes.bat");
            string filetext = "chcp 65001" + Environment.NewLine;
            bool retainStructure = (settings.retainStructure.Length > 0);
            foreach (VideoInfo item in mediaList.Where(x => (x.remove ?? false) && !String.IsNullOrEmpty(x.path)).OrderBy(p => p.triggerId))
            {
                filetext += "DEL \"" + batchPath(item.path) + "\" /f" + Environment.NewLine;
                if (retainStructure && settings.retainStructure.Where(p => item.path.ToLower().StartsWith(p.ToLower())).Any())
                {
                    VideoInfo org = mediaList.Where(x => x.id == item.triggerId).FirstOrDefault();
                    if (org == null) continue;
                    filetext += "MOVE \"" + batchPath(org.path) + "\" \""+ batchPath(item.path) + "\"" + Environment.NewLine;
                }

            }
            File.WriteAllText(filepath, filetext);
        }
  
        public void generateEncoding(List<VideoInfo> mediaList)
        {   
            // TODO: Switch to using MediaInfo
            // dotnet add package MediaInfo.Wrapper.Core 

            using (StreamWriter outputFile = new StreamWriter(Path.Combine(this.outputFolder, "encoding-template.bat")))
            {
                outputFile.WriteLine("chcp 65001"); // Set codepage
                outputFile.WriteLine("REM     This encoding template is a suggestion with highest bitrates at the top");
                outputFile.WriteLine("REM     NOTE! Many files will NOT be valid to encode, the bitrate may be due to faulty 'duration'-mesaurements");
                outputFile.WriteLine("REM     ------------------------------------------------------------------------------------------------------");
                outputFile.WriteLine("");

                // mpg/wmv have unreliable duration and are usually low bitrate
                foreach (VideoInfo item in mediaList.Where(x => x.duration != null && x.duration > 1
                        && !x.path.ToLower().EndsWith(".mpg")
                        && !x.path.ToLower().EndsWith(".mpeg")
                        && !x.path.ToLower().EndsWith(".wmv")).OrderByDescending(p => p.fileSize / p.duration))
                {
                    double bitrateKbps = Math.Round(((item.bitrate ?? 1000) / 1000));
                    string modifiedPath = item.path.Substring(0, item.path.LastIndexOf(".")) + "_720p.mp4"; // output ext is always mp4 

                    outputFile.WriteLine($"REM  Total bitrate: {bitrateKbps} kbps");
                    // -c:v libx265         use h.265 encoding
                    // -vtag hvc1           set the video container to hvc1
                    // -vf scale=1280:720   scale video to 720p
                    // -crf XX              set video conpression (Constant Rate Factor). 0-16=lossless, 20=Very good, 24=Good, 28-51=degraded 
                    // crf values explained here: https://goughlui.com/2016/08/27/video-compression-testing-x264-vs-x265-crf-in-handbrake-0-10-5/
                    // This encoding is CPU-only as hardware accelerated encoders do not support CRF
                    // -preset superfast    Only at the "slow"-preset is there a quality gain over "superfast"
                    //  preset values are explained here: https://www.youtube.com/watch?v=bzgFL-gNF9g
                    outputFile.WriteLine($"..\\ffmpeg\\ffmpeg -n -i \"{item.path}\" -c:v libx265 -vtag hvc1 -vf scale=1280:720 -crf 24 -preset superfast  -c:a copy \"{modifiedPath}\"");
                    outputFile.WriteLine();

                }
            }
        }


        public static string SpaceNotice(long space, string comment)
        {
            decimal mb = Math.Round((decimal)space / (1024 * 1024), 0);
            decimal gb = Math.Round(mb / 1024, 1);
            return ($"<h4>{comment} {mb} MB  / {gb} GB </h4>");

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
            using (StreamWriter outputFile = new StreamWriter(Path.Combine(this.outputFolder, "report.html")))
            {
                long spaceSaved = 0;
                long spaceUsedByOriginals = 0;
                string renameDraft = "";
                bool retainIncomingFilenames = (settings.retainIncomingFilenames.Length > 0);
                outputFile.WriteLine("<h3>Delete report</h3>");
                foreach (VideoInfo item in mediaList.Where(x => (x.remove ?? false)).OrderBy(p => p.triggerId))
                {
                    spaceSaved += item.fileSize ?? 0;
                    VideoInfo org = mediaList.Where(x => x.id == item.triggerId).FirstOrDefault();
                    if (org != null)
                    {
                        spaceUsedByOriginals += org.fileSize ?? 0;
                        int orgSize = (int)(org.fileSize / (1024 * 1024));
                        outputFile.WriteLine("ORIGINAL: " + org.path + "  (" + orgSize + " MB )<br>");
                        outputFile.WriteLine("<img src='file:///" + screenshotPath(org, 1) + "'>");
                        outputFile.WriteLine("<img src='file:///" + screenshotPath(org, 2) + "'>");
                        outputFile.WriteLine("<img src='file:///" + screenshotPath(org, 3) + "'><br>");

                        if (retainIncomingFilenames && settings.retainIncomingFilenames.Where(p => org.path.ToLower().StartsWith(p.ToLower())).Any())
                        {
                            renameDraft += "REM ** " + item.path + " ** " + Environment.NewLine; 
                            renameDraft += "ren \"" + org.path + "\" \""+ org.path + "\"" + Environment.NewLine + Environment.NewLine;
                        }
                    }

                    int dupeSize = (int)(item.fileSize / (1024 * 1024));
                    if (dupeSize>1) {
                        if (org == null)     outputFile.WriteLine("<b>Original not found - check alternative reason</b><br>");
                        outputFile.WriteLine("DELETE: " + item.path + "  (" + dupeSize + " MB )<br>");
                        outputFile.WriteLine("<img src='file:///" + screenshotPath(item, 1) + "'>");
                        outputFile.WriteLine("<img src='file:///" + screenshotPath(item, 2) + "'>");
                        outputFile.WriteLine("<img src='file:///" + screenshotPath(item, 3) + "'><br>");
                        outputFile.WriteLine("<b>" + item.reason + "</b><br>");
                        outputFile.WriteLine("<hr>");
                    }
                }

                if (renameDraft!="") {
                    File.WriteAllText(Path.Combine(this.outputFolder, "rename-draft.bat"), renameDraft);
                }

                outputFile.WriteLine(SpaceNotice(spaceUsedByOriginals, "Space used by originals: "));
                outputFile.WriteLine(SpaceNotice(spaceSaved, "Space used by dupes: "));
            }
        }
        public void generateIndexReport(List<VideoInfo> mediaList)
        {
            using (StreamWriter outputFile = new StreamWriter(Path.Combine(this.outputFolder, "indexReport.html")))
            {
                outputFile.WriteLine("<h3>Index report</h3>");
                foreach (VideoInfo item in mediaList.OrderBy(p => p.path))
                {
                        outputFile.WriteLine(item.path + "  (" + item.fileSize + " MB )<br>");
                        outputFile.WriteLine("<img src='file:///" + screenshotPath(item, 1) + "'>");
                        outputFile.WriteLine("<img src='file:///" + screenshotPath(item, 2) + "'>");
                        outputFile.WriteLine("<img src='file:///" + screenshotPath(item, 3) + "'><br>");
                        outputFile.WriteLine("<hr><br>");
               }
            }
        }

    }
}
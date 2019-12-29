using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xabe.FFmpeg.Enums;

namespace deepduplicates
{
    public class FileHandler
    {
        public string screenshotFolder { get; set; }
        public string outputFolder { get; set; }
        public IEnumerable<string> allFiles { get; set; }
        public int allFilesCount { get; set; }

        private string appFolder = Directory.GetCurrentDirectory();

        public Settings settings = null;

        private void InitConfig()
        {
            
            string settingsPath = Path.Combine(appFolder, "settings.json");
            string settings_json = "";
            try {
                 settings_json = System.IO.File.ReadAllText(settingsPath);
            } catch {
                // Presumably settings.json do not exist - set default values and save
                Console.WriteLine("Can't open 'settings.json' - creating new with default values.");
                this.settings = new Settings();
                this.settings.minVideoLength = 3;
                this.settings.contentFolders = new string[] { @"c:\" };
                this.settings.logInterval = 100;
                this.settings.ffmpegFolder = @"C:\dev\ffmpeg\bin";
                File.WriteAllText(settingsPath, JsonSerializer.Serialize(this.settings));
            }
            
            if (this.settings == null) this.settings = JsonSerializer.Deserialize<Settings>(settings_json);
        }

        public string screenshotPath(VideoInfo item, int prefix){
            return(Path.Combine(this.screenshotFolder, item.id + "_" + prefix + FileExtensions.Png));
        }
        public FileHandler()
        {
            InitConfig();

            this.screenshotFolder = Path.Combine(this.appFolder, "_screens");
            Directory.CreateDirectory(this.screenshotFolder);

            this.outputFolder = Path.Combine(this.appFolder, "output");
            Directory.CreateDirectory(this.outputFolder);

            this.allFiles = null;
            foreach (string contentFolder in settings.contentFolders)
            {
                Console.WriteLine("Indexing all files in: " + contentFolder + "...");
                IEnumerable<string> files = GetFiles(contentFolder, new[] { ".avi", ".divx", ".mp4", ".m4v", ".mov" });
                if (allFiles == null)
                {
                    allFiles = files;
                }
                else
                {
                    allFiles = allFiles.Concat(files);
                }

                Console.WriteLine("Found a total of " + allFiles.Count() + " video files.");
            }
            this.allFilesCount = allFiles.Count();
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
            catch
            {
                return (-1);
            }
        }

        private IEnumerable<string> GetFiles(string path, string[] ext)
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
                catch (UnauthorizedAccessException){
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
                catch (UnauthorizedAccessException){
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
                        if (Array.IndexOf(ext, Path.GetExtension(files[i])) > -1) yield return files[i];
                    }
                }
            }
        }

        public void generateBatchFile(List<VideoInfo> mediaList)
        {
            string filepath = Path.Combine(this.outputFolder, "delete_all_dupes.bat");
            string filetext = "chcp 65001" + Environment.NewLine;
            foreach (VideoInfo item in mediaList.Where(x => x.remove))
            {
                filetext += "DEL \"" + item.path + "\"" + Environment.NewLine;
            }
            File.WriteAllText(filepath, filetext, Encoding.UTF8);
        }

        public void generateReport(List<VideoInfo> mediaList)
        {
            using (StreamWriter outputFile = new StreamWriter(Path.Combine(this.outputFolder, "report.html")))
            {
                long spaceSaved = 0;
                outputFile.WriteLine("<h3>Delete report</h3>");
                foreach (VideoInfo item in mediaList.Where(x => x.remove).OrderBy(p => p.triggerId))
                {
                    spaceSaved += item.fileSize ?? 0;
                    VideoInfo org = mediaList.Where(x => x.id == item.triggerId).FirstOrDefault();
                    if (org != null)
                    {
                        int orgSize = (int)(org.fileSize / (1024 * 1024));
                        outputFile.WriteLine("ORIGINAL: " + org.path + "  (" + orgSize + " MB )<br>");
                        outputFile.WriteLine("<img src='file:///" + screenshotPath(org,1) + "'>");
                        outputFile.WriteLine("<img src='file:///" + screenshotPath(org,2) + "'><br>");

                    }
                    else
                    {
                        outputFile.WriteLine("<b>Original not found - check alternative reason</b><br>");
                    }
                    int dupeSize = (int)(item.fileSize / (1024 * 1024));
                    outputFile.WriteLine("DELETE: " + item.path + "  (" + dupeSize + " MB )<br>");
                    outputFile.WriteLine("<img src='file:///" + screenshotPath(item,1) + "'>");
                    outputFile.WriteLine("<img src='file:///" + screenshotPath(item,2) + "'><br>");
                    outputFile.WriteLine("<b>" + item.reason + "</b><br>");
                    outputFile.WriteLine("<hr>");
                }
                decimal mb = Math.Round((decimal)spaceSaved / (1024 * 1024), 0);
                decimal gb = Math.Round(mb / 1024, 1);
                outputFile.WriteLine("<h4>Space saved: " + mb + "MB  / " + gb + " GB </h4>");
            }
        }

    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xabe.FFmpeg;


namespace deepduplicates
{
    public class VideoHandler
    {
        private FileHandler fileHandler;
        public VideoHandler(FileHandler fileHandler){
            this.fileHandler = fileHandler;
        }

        /// <summary>
        /// Get VideoMetadata from all included files in the filesystem
        /// Use DB as cache 
        /// </summary>
        /// <param name="db">databasecontext</param>
        /// <returns>Colleciton of VideoInfo. All metadata from database. On the first run only video path and duration is present</returns>
        public async Task<List<VideoInfo>> getVideoMetadata(VideoInfoContext db)
        {

            Console.WriteLine("Reading db...");
            List<VideoInfo> fullTableScan = db.VideoInfos.ToList();
            List<VideoInfo> mediaList = new List<VideoInfo>();

            Console.WriteLine("Found: " + fullTableScan.Count());

            //await Xabe.FFmpeg.FFmpeg.GetLatestVersion();
            //Xabe.FFmpeg.FFmpeg.ExecutablesPath = fileHandler.settings.ffmpegFolder;

            int index = 0;
            foreach (string path in fileHandler.allFiles)
            {
                index++;
                if (index % fileHandler.settings.logInterval == 0) Console.WriteLine(index + "/" + fileHandler.allFilesCount + " checking: " + path);

                VideoInfo item = fullTableScan.Where(vi => vi.path == path).FirstOrDefault();
                if (item != null) mediaList.Add(item);
                bool newItem = false;

                if (item == null || item.duration == -1)
                {
                    if (item == null)
                    {
                        item = new VideoInfo();
                        newItem = true;
                    }
                    // Console.WriteLine("Reading size ...");
                    item.fileSize = fileHandler.GetFileSize(path);
                    if (item.fileSize == -1)
                    {
                        Console.WriteLine("Can't read file - skipping");
                        continue;
                    }

                    item.path = path;
                    item.duration = -1;

                    //Console.WriteLine("Reading video duration...");
                    try
                    {
                        IMediaInfo info = await Xabe.FFmpeg.MediaInfo.Get(path);
                        int duration = (int)Math.Round(info.Duration.TotalSeconds, 0);
                        item.duration = duration;
                    }
                    catch
                    {
                        item.remove = true;
                        item.reason = "Can't read videofile";
                    }

                    if (newItem)
                    {
                        db.VideoInfos.Add(item);
                        mediaList.Add(item);
                    }
                    await db.SaveChangesAsync();
                    Console.WriteLine(index + "/" + fileHandler.allFilesCount + " - Video metadata retrieved. Saved to db: " + item);
                }
            }
            Console.WriteLine("mediaList length: " + mediaList.Count());

            return (mediaList);
        }



        private async Task<long> saveScreenshotReturnChecksumAsync(IMediaInfo info, string screenshotPath, ImageHandler imageHandler, int point)
        {
            if (!File.Exists(screenshotPath)) await imageHandler.takeScreenshot(info, screenshotPath, TimeSpan.FromSeconds(point), 320, 240).Start();
            return (imageHandler.imageChecksum(screenshotPath));
        }

        public async Task generateAllScreenshotsAsync(List<VideoInfo> mediaList, ImageHandler imageHandler, VideoInfoContext db)
        {
            List<int?> lengthDubes = mediaList.GroupBy(x => x.duration).Where(g => g.Count() > 1).Select(y => y.Key).ToList();
            Console.WriteLine("");
            Console.WriteLine("Screenshot videos of the same duration:");

            int index = 0;
            // Only make screenshots of files that have a duration shared by 2 or more a other videos. If the duration is unique the video should be unique.
            List<VideoInfo> screenshotList = mediaList.Where(p => lengthDubes.Contains(p.duration) && !p.remove).ToList();
            int screenshotListLength = screenshotList.Count();
            foreach (VideoInfo item in screenshotList)
            {
                index++;
                if (item.image1Checksum == 0 || item.image1Checksum == null || item.image2Checksum == 0 || item.image2Checksum == null)
                {

                    int point1 = (item.duration ?? 5) / 3;
                    int point2 = ((item.duration ?? 5) / 3) * 2;
                    try
                    {
                        IMediaInfo info = await Xabe.FFmpeg.MediaInfo.Get(item.path);
                        item.image1Checksum = await saveScreenshotReturnChecksumAsync(info, fileHandler.screenshotPath(item, 1), imageHandler, point1);
                        item.image2Checksum = await saveScreenshotReturnChecksumAsync(info, fileHandler.screenshotPath(item, 2), imageHandler, point2);

                        await db.SaveChangesAsync();
                        Console.WriteLine("Created screenshots for: " + index + "/" + screenshotListLength + " - " + item);
                    }
                    catch (Exception e)
                    {
                        int showFirst = 60;
                        if (e.Message.Length < showFirst) showFirst = e.Message.Length;
                        string msg = "Screenshot failed! item.duration: " + item.duration + " point1: " + point1 + " point2: " + point2 + "  \nMsg:" + e.Message.Substring(0, showFirst);
                        if (e.Message.Contains("Seek can not be greater than video"))
                        {
                            Console.WriteLine("For some reason - Xabe is unable to create screenshot for this video. It seems to be a bug so we will ignore this error.");
                        }
                        else
                        {
                            item.remove = true;
                            item.reason = msg;
                        }
                        Console.WriteLine(msg);
                    }

                }
                else
                {
                    if (index % fileHandler.settings.logInterval == 0) Console.WriteLine("Skip: " + index + "/" + screenshotListLength + " Checksum found in db " + item);
                }

                if (item.image1hash == null)
                {
                    item.image1hash = imageHandler.ImageHash(fileHandler.screenshotPath(item, 1));
                    item.image2hash = imageHandler.ImageHash(fileHandler.screenshotPath(item, 2));
                }
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Model;

namespace deepduplicates
{
    public class VideoHandler
    {
        private FileHandler fileHandler;
        private ImageHandler imageHandler;
        public VideoHandler(FileHandler fileHandler, ImageHandler imageHandler)
        {
            this.fileHandler = fileHandler;
            this.imageHandler = imageHandler;
        }

        public void ShowTimeStamp(Stopwatch stopWatch, string comment)
        {
            TimeSpan ts = stopWatch.Elapsed;
            string elapsedTime = String.Format("{0:00}:{1:00}:{2:00}.{3:00}",
    ts.Hours, ts.Minutes, ts.Seconds,
    ts.Milliseconds / 10);
            Console.WriteLine(comment + elapsedTime);
        }

        /// <summary>
        /// Get VideoMetadata from all included files in the filesystem
        /// Use DB as cache 
        /// </summary>
        /// <param name="db">databasecontext</param>
        /// <returns>Colleciton of VideoInfo. All metadata from database. On the first run only video path and duration is present</returns>
        public async Task<List<VideoInfo>> saveVideoMetadataAndScreenshots(VideoInfoContext db)
        {

            Console.WriteLine("Reading db...");
            List<VideoInfo> fullTableScan = db.VideoInfos.ToList();
            List<VideoInfo> mediaList = new List<VideoInfo>();

            int DBCount = fullTableScan.Count();
            if (DBCount > 0)
            {
                Console.WriteLine("Found: " + DBCount);
            }
            else
            {
                Console.WriteLine("Database is new");
            }
            await Xabe.FFmpeg.FFmpeg.GetLatestVersion();
            List<TimeSpan> timediag = new List<TimeSpan>();
            int index = 0;
            //Stopwatch stopWatch = new Stopwatch();
            Stopwatch stopWatch_total = new Stopwatch();
            stopWatch_total.Start();
            int fullProcessedItems = 0;

            foreach (string path in fileHandler.allFiles)
            {
                index++;
                //stopWatch.Start();
                if (index % fileHandler.settings.logInterval == 0) Console.WriteLine(index + "/" + fileHandler.allFilesCount + " checking: " + path);

                bool newItem = false;
                VideoInfo item = fullTableScan.Where(vi => vi.path == path).FirstOrDefault();
                if (item != null)
                {
                    mediaList.Add(item);
                    if (item.remove ?? false || item.formatNotSupported) continue; // this has already finished processing
                }
                else
                {
                    item = new VideoInfo();
                    newItem = true;
                    db.VideoInfos.Add(item);
                    mediaList.Add(item);
                }

                if ((newItem || item.duration == -1 || item.image1Checksum == null || item.image1hash_blob == null) && item.formatNotSupported != true)
                {
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
                    IMediaInfo info = null;
                    try
                    {
                        info = await Xabe.FFmpeg.MediaInfo.Get(path);
                        int duration = (int)Math.Round(info.Duration.TotalSeconds, 0);
                        item.duration = duration;
                        //ShowTimeStamp(stopWatch, "Get metadata: ");
                        fullProcessedItems++;
                    }
                    catch
                    {
                        item.remove = true;
                        item.reason = "Can't read videofile";
                    }

                    item = RecommendationHandler.removingShortVideo(item, fileHandler.settings.minVideoLength);

                    if (item.image1Checksum == null && info != null && !(item.remove ?? false))
                    {
                        //Console.WriteLine("Generating screenshots");
                        if (item.id == 0) await db.SaveChangesAsync(); // Must save before screenshots so image has ID
                        await generateOneSetOfScreenshotsAsync(item, info, imageHandler);
                        //ShowTimeStamp(stopWatch, "Get Screenshots: ");
                    }

                    if (item.image1hash == null && !(item.remove ?? false))
                    {
                        item.image1hash = imageHandler.ImageHash(fileHandler.screenshotPath(item, 1));
                        item.image2hash = imageHandler.ImageHash(fileHandler.screenshotPath(item, 2));
                        item.image3hash = imageHandler.ImageHash(fileHandler.screenshotPath(item, 3));

                        item.image1hash_blob = imageHandler.ImageHashToByteArray(item.image1hash);
                        item.image2hash_blob = imageHandler.ImageHashToByteArray(item.image2hash);
                        item.image3hash_blob = imageHandler.ImageHashToByteArray(item.image3hash);
                        //ShowTimeStamp(stopWatch, "Encode hash: ");
                    }

                    if (index % 20 == 0)
                    {
                        await db.SaveChangesAsync();
                        ShowTimeStamp(stopWatch_total, "All full process items: " + fullProcessedItems + " took a total of: ");
                        //ShowTimeStamp(stopWatch, "Save last 20 items to db.");
                    }
                    //ShowTimeStamp(stopWatch, "Item Complete: ");
                    Console.WriteLine(index + "/" + fileHandler.allFilesCount + " - Video metadata retrieved. Saved to db: " + item);
                    //stopWatch.Reset();
                }

                if (item.image1hash == null && item.image1hash_blob != null)
                {
                    item.image1hash = imageHandler.ByteArrayToImageHash(item.image1hash_blob);
                    if (item.image2hash_blob != null) item.image2hash = imageHandler.ByteArrayToImageHash(item.image2hash_blob);
                    if (item.image3hash_blob != null) item.image3hash = imageHandler.ByteArrayToImageHash(item.image3hash_blob);
                }
                
            }
            await db.SaveChangesAsync();
            Console.WriteLine("Video mediaList length: " + mediaList.Count());
            ShowTimeStamp(stopWatch_total, "All full process items: " + fullProcessedItems + " took a total of: ");
            return (mediaList);
        }

        private async Task<long> saveScreenshotReturnChecksumAsync(IMediaInfo info, string screenshotPath, ImageHandler imageHandler, int point)
        {
            if (!File.Exists(screenshotPath)) await imageHandler.takeScreenshot(info, screenshotPath, TimeSpan.FromSeconds(point), 240, 160).Start();
            return (imageHandler.imageChecksum(screenshotPath));
        }

        public async Task generateOneSetOfScreenshotsAsync(VideoInfo item, IMediaInfo info, ImageHandler imageHandler)
        {
            if (item.duration == null || item.duration == -1) return;
            int point1 = item.duration.Value / 4;
            int point2 = (item.duration.Value / 4) * 2;
            int point3 = (item.duration.Value / 4) * 3;
            string fatalErrorMsg = "Most likely caused by an invalid or incomplete video. Attempt to open the video in an appropriate videoplayer to confirm this before deleting.";

            try
            {
                item.image1Checksum = await saveScreenshotReturnChecksumAsync(info, fileHandler.screenshotPath(item, 1), imageHandler, point1);
                item.image2Checksum = await saveScreenshotReturnChecksumAsync(info, fileHandler.screenshotPath(item, 2), imageHandler, point2);
                item.image3Checksum = await saveScreenshotReturnChecksumAsync(info, fileHandler.screenshotPath(item, 3), imageHandler, point3);
            }
            catch (Xabe.FFmpeg.Exceptions.UnknownDecoderException)
            {
                item.formatNotSupported = true;
                item.reason = "Screenshot failed! UnknownDecoderException - The most likely reason is that Ffmpeg does not support this codec-variant. The video will be disregarded in the rest of the process.";
            }
            catch (Xabe.FFmpeg.Exceptions.ConversionException)
            {
                item.remove = true;
                item.reason = "Screenshot failed! ConversionException. " + fatalErrorMsg;
                if (item.image3Checksum != null) item.reason += "It seems we managed to create all screenshots - so the video might be playable.";
            }
            catch (Exception e)
            {
                if (e.Message.Contains("Seek can not be greater than video"))
                {
                    item.formatNotSupported = true;
                    item.reason = "Unable to create screenshot for this video.";
                    Console.WriteLine("For some reason - Xabe is unable to create screenshot for this video. It seems to be a bug so we will ignore this error.");
                }
                else
                {
                    int showFirst = 150;
                    if (e.Message.Length < showFirst) showFirst = e.Message.Length;
                    string msg = "Screenshot failed! Generic error. " + fatalErrorMsg + " <!-- " + e.Message.Substring(0, showFirst) + "-->";
                    Console.WriteLine(msg);
                    item.remove = true;
                    item.reason = msg;
                }
            }

        }
    }
}
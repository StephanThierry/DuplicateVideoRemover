using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Model;
using Xabe.FFmpeg.Streams;

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

        public static void ShowTimeStamp(Stopwatch stopWatch, string comment)
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
            => await saveVideoMetadataAndScreenshots(db, null, CancellationToken.None, null);

        /// <summary>
        /// True when the file at this path is not the file the database row was made from: its size on disk is not the size that was
        /// read. (The database is keyed by path, so a file that is replaced - an unfinished copy that was finished or encoded again - would
        /// keep the row of the old one for ever: the duration and pictures of a few seconds of video, never matching the real file.)
        /// </summary>
        public static bool IsStale(VideoInfo item, long? sizeOnDisk) =>
            item.fileSize is long was && was > 0 && sizeOnDisk is long now && now > 0 && now != was;

        /// <summary>
        /// True when the file was written after its pictures were made: it was not finished when it was read. A download that reserves the
        /// whole file at once keeps its size, so <see cref="IsStale"/> does not see it; the pictures then show a broken frame for ever.
        /// A minute of slack covers clocks that differ a little between the NAS and this computer.
        /// </summary>
        public static bool WrittenAfterPictures(DateTime? fileWrittenUtc, DateTime? picturesMadeUtc) =>
            fileWrittenUtc is DateTime f && picturesMadeUtc is DateTime p && f > p.AddMinutes(1);

        /// <summary>
        /// True when the row's pictures were left over from a read that failed: an earlier version took them again on the next scan
        /// without making new ones, so the row looks read but its pictures (and hashes) are of a broken file.
        /// </summary>
        public static bool HasPicturesFromFailedRead(VideoInfo item) =>
            item.image1Checksum != null && item.formatNotSupported != true && item.reason != null && item.reason.StartsWith("Screenshot failed");

        private DateTime? PicturesMadeUtc(VideoInfo item)
        {
            try { var shot = new System.IO.FileInfo(fileHandler.screenshotPath(item, 1)); return shot.Exists ? shot.LastWriteTimeUtc : null; }
            catch { return null; }
        }

        // Forgets everything read from the old file, so the row is read again: its pictures too (they are kept by row id and reused)
        public void ForgetWhatWasRead(VideoInfo item)
        {
            for (int n = 1; n <= 3; n++)
            {
                try { string shot = fileHandler.screenshotPath(item, n); if (File.Exists(shot)) File.Delete(shot); }
                catch (Exception) { /* in use: it is made again over the old one */ }
            }
            item.duration = -1; item.bitrate = null; item.reason = null; item.formatNotSupported = false;
            item.image1Checksum = item.image2Checksum = item.image3Checksum = null;
            item.image1hash_blob = item.image2hash_blob = item.image3hash_blob = null;
            item.image1hash = item.image2hash = item.image3hash = null;
        }

        /// <summary>Called for each video as the scan gets to it in the database (read before or just read).</summary>
        public Action<VideoInfo> OnSeen { get; set; }

        /// <summary>Called when a new video has been read (its pictures are made).</summary>
        public Action<VideoInfo> OnRead { get; set; }

        /// <summary>Files that needed reading in the last run, and the seconds spent on them (for time estimates).</summary>
        public int LastNewFiles { get; private set; }
        public double LastNewSeconds { get; private set; }

        /// <param name="progress">Receives progress for the interfaces (optional)</param>
        /// <param name="ct">Stops the scan; what was read so far is saved and reused next time</param>
        /// <param name="historicalSecondsPerNewFile">Average from earlier runs, used for the first estimate</param>
        public async Task<List<VideoInfo>> saveVideoMetadataAndScreenshots(VideoInfoContext db, IProgress<ScanProgress> progress,
            CancellationToken ct, double? historicalSecondsPerNewFile)
        {
            Console.WriteLine("Reading db...");
            List<VideoInfo> fullTableScan = db.VideoInfos.ToList();
            Console.WriteLine("Db in memory");
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
            List<TimeSpan> timediag = new List<TimeSpan>();
            int index = 0;
            //Stopwatch stopWatch = new Stopwatch();
            Stopwatch stopWatch_total = new Stopwatch();
            stopWatch_total.Start();
            int fullProcessedItems = 0;

            var query = (from path in fileHandler.allFiles
                        join item in fullTableScan on path equals item.path into tmp
                        from subItem in tmp.DefaultIfEmpty()
                        select new VideoInfoWrapper { path = path, item = subItem }).ToList();

            // A file that is not the one that was read any more (another size than the row says, or written after its pictures were
            // made), or whose pictures are left over from a failed read, is read again
            int changedFiles = 0;
            foreach (var w in query)
            {
                if (w.item == null) continue;
                string why = null;
                if (fileHandler.allFileSizes.TryGetValue(w.path, out long sizeNow) && IsStale(w.item, sizeNow))
                    why = $"changed since it was read ({Fmt.Size(w.item.fileSize ?? 0)} then, {Fmt.Size(sizeNow)} now)";
                else if (HasPicturesFromFailedRead(w.item))
                    why = "its pictures are from a read that failed";
                else if (w.item.image1Checksum != null && fileHandler.allFileTimes.TryGetValue(w.path, out var written) && WrittenAfterPictures(written, PicturesMadeUtc(w.item)))
                    why = "written after its pictures were made";
                if (why == null) continue;
                Console.WriteLine($"Reading again, {why}: {w.path}");
                ForgetWhatWasRead(w.item);
                changedFiles++;
            }
            if (changedFiles > 0) Console.WriteLine($"{changedFiles} files changed since they were read and are read again.");

            // Workload: files not in the database yet, or incomplete there. Cached files take almost no time.
            int newTotal = query.Count(w => w.item == null ||
                ((w.item.duration == -1 || w.item.image1Checksum == null || w.item.image1hash_blob == null) && w.item.formatNotSupported != true));
            int newDone = 0, failed = 0;
            double newSeconds = 0;
            var lastReport = Stopwatch.StartNew();

            void Report(string current, bool force)
            {
                if (progress == null || (!force && lastReport.ElapsedMilliseconds < 250)) return;
                lastReport.Restart();
                double? perFile = newDone >= 3 ? newSeconds / newDone : historicalSecondsPerNewFile;
                progress.Report(new ScanProgress
                {
                    Phase = ScanPhase.ReadingVideos,
                    Message = "Reading new videos",
                    FilesFound = query.Count,
                    Checked = index,
                    NewTotal = newTotal,
                    NewDone = newDone,
                    Failed = failed,
                    CurrentFile = current,
                    Elapsed = stopWatch_total.Elapsed,
                    SecondsPerNewFile = perFile,
                    EstimateFromHistory = newDone < 3 && historicalSecondsPerNewFile.HasValue,
                    Remaining = perFile.HasValue ? TimeSpan.FromSeconds(perFile.Value * (newTotal - newDone)) : null
                });
            }

            foreach (VideoInfoWrapper wrapper in query)
            {
                if (ct.IsCancellationRequested)
                {
                    await db.SaveChangesAsync(); // keep what was read, the next run continues from here
                    LastNewFiles = newDone; LastNewSeconds = newSeconds;
                    ct.ThrowIfCancellationRequested();
                }
                index++;
                Report(wrapper.path, false);

                // A file another process is still writing: skip it now, the next scan reads it
                if ((wrapper.item == null || ((wrapper.item.duration == -1 || wrapper.item.image1Checksum == null || wrapper.item.image1hash_blob == null) && wrapper.item.formatNotSupported != true))
                    && FileHandler.IsBeingWritten(wrapper.path))
                {
                    Console.WriteLine("Skipping, still being written: " + wrapper.path);
                    newTotal--;
                    continue;
                }
                //stopWatch.Start();
                if (index % fileHandler.settings.logInterval == 0) Console.WriteLine(index + "/" + fileHandler.allFilesCount + " checking: " + wrapper.path);

                bool newItem = false;
                if (wrapper.item != null)
                {
                    wrapper.item.remove = false; // reprocess all files previously marked for removal
                    mediaList.Add(wrapper.item);
                    //if (wrapper.item.remove ?? false || wrapper.item.formatNotSupported) continue; // this has already finished processing
                }
                else
                {
                    wrapper.item = new VideoInfo();
                    newItem = true;
                    db.VideoInfos.Add(wrapper.item);
                    mediaList.Add(wrapper.item);
                }

                if ((newItem || wrapper.item.duration == -1 || wrapper.item.image1Checksum == null || wrapper.item.image1hash_blob == null) && wrapper.item.formatNotSupported != true)
                {
                    var itemTimer = Stopwatch.StartNew();
                    try
                    {
                    // Console.WriteLine("Reading size ...");
                    wrapper.item.fileSize = fileHandler.GetFileSize(wrapper.path);
                    if (wrapper.item.fileSize == -1)
                    {
                        Console.WriteLine("Can't read file - skipping");
                        continue;
                    }

                    wrapper.item.path = wrapper.path;
                    wrapper.item.duration = -1;
                    wrapper.item.reason = null;   // what an earlier, failed read said no longer holds

                    //Console.WriteLine("Reading video duration...");
                    IMediaInfo info = null;
                    try
                    {
                        info = await Xabe.FFmpeg.MediaInfo.Get(wrapper.path);
                        int duration = (int)Math.Round(info.Duration.TotalSeconds, 0);
                        foreach (IVideoStream stream in info.VideoStreams)
                        {
                            wrapper.item.bitrate = stream.Bitrate;  // same bitrate of last stream (usually there is only one)

                        }
                        wrapper.item.duration = duration;
                        //ShowTimeStamp(stopWatch, "Get metadata: ");
                        fullProcessedItems++;
                    }
                    catch
                    {
                        wrapper.item.remove = true;
                        wrapper.item.reason = "Can't read videofile";
                    }

                    wrapper.item = RecommendationHandler.removingShortVideo(wrapper.item, fileHandler.settings.minVideoLength, fileHandler.settings.minVideoSizeKb);

                    if (wrapper.item.image1Checksum == null && info != null && !(wrapper.item.remove ?? false))
                    {
                        // Generating screenshots
                        if (wrapper.item.id == 0) await db.SaveChangesAsync(); // Must save before screenshots so image has ID
                        await generateOneSetOfScreenshotsAsync(wrapper.item, info, imageHandler);
                        //ShowTimeStamp(stopWatch, "Get Screenshots: ");
                    }

                    if (wrapper.item.image1hash == null && !(wrapper.item.remove ?? false))
                    {
                        wrapper.item.image1hash_blob = imageHandler.ImageHashToByteArray(imageHandler.ImageHash(fileHandler.screenshotPath(wrapper.item, 1)));
                        wrapper.item.image2hash_blob = imageHandler.ImageHashToByteArray(imageHandler.ImageHash(fileHandler.screenshotPath(wrapper.item, 2)));
                        wrapper.item.image3hash_blob = imageHandler.ImageHashToByteArray(imageHandler.ImageHash(fileHandler.screenshotPath(wrapper.item, 3)));
                    }

                    if (index % 20 == 0)
                    {
                        await db.SaveChangesAsync();
                        ShowTimeStamp(stopWatch_total, "All full process items: " + fullProcessedItems + " took a total of: ");
                    }
                    Console.WriteLine(index + "/" + fileHandler.allFilesCount + " - Video metadata retrieved. Saved to db: " + wrapper.item);
                    }
                    finally
                    {
                        newDone++;
                        newSeconds += itemTimer.Elapsed.TotalSeconds;
                        if (wrapper.item.remove == true || wrapper.item.duration == -1) failed++;
                        else if (wrapper.item.id != 0) OnRead?.Invoke(wrapper.item);   // a new video was read: its picture is shown at once
                        Report(wrapper.path, true);
                    }
                }

                if (wrapper.item.image1hash_blob != null) wrapper.item.image1hash = imageHandler.ByteArrayToImageHash(wrapper.item.image1hash_blob);
                if (wrapper.item.image2hash_blob != null) wrapper.item.image2hash = imageHandler.ByteArrayToImageHash(wrapper.item.image2hash_blob);
                if (wrapper.item.image3hash_blob != null) wrapper.item.image3hash = imageHandler.ByteArrayToImageHash(wrapper.item.image3hash_blob);

                OnSeen?.Invoke(wrapper.item);   // the video the scan is at (a live picture is taken from it every 2.5 seconds)
            }
            await db.SaveChangesAsync();
            LastNewFiles = newDone; LastNewSeconds = newSeconds;
            Report("", true);
            Console.WriteLine("Video mediaList length: " + mediaList.Count());
            ShowTimeStamp(stopWatch_total, "All full process items: " + fullProcessedItems + " took a total of: ");
            return (mediaList);
        }

        // Always a new picture: one already there is left over from a read that failed or was stopped (this only runs for a row without
        // pictures), and reusing it gave the row a broken frame for good
        private async Task<rgb[]> saveScreenshotReturnChecksumAsync(IMediaInfo info, string screenshotPath, ImageHandler imageHandler, int point)
        {
            try { if (File.Exists(screenshotPath)) File.Delete(screenshotPath); } catch (IOException) { /* in use: FFmpeg writes over it */ }
            await imageHandler.takeScreenshot(info.FileInfo.FullName, screenshotPath, point, 240, 160);
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
                List<Task<rgb[]>> tasks = new List<Task<rgb[]>>();
                tasks.Add(saveScreenshotReturnChecksumAsync(info, fileHandler.screenshotPath(item, 1), imageHandler, point1));
                tasks.Add(saveScreenshotReturnChecksumAsync(info, fileHandler.screenshotPath(item, 2), imageHandler, point2));
                tasks.Add(saveScreenshotReturnChecksumAsync(info, fileHandler.screenshotPath(item, 3), imageHandler, point3));
                
                rgb[][] responses = await Task.WhenAll(tasks);
                item.image1Checksum = responses[0];
                item.image2Checksum = responses[1];
                item.image3Checksum = responses[2];

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
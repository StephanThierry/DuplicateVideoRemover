using System;
using System.Collections.Generic;
using System.Threading.Tasks;

//https://xabe.net/net-video-converter-toturial/

namespace deepduplicates
{
    class Program
    {
        // Accuracy tested to 99,8% - so check the report for false positives - in every 1000 matches aprox. 2-3 are likely wrong!
        static async Task Main(string[] args)
        {
            VideoInfoContext db = new VideoInfoContext();
            db.Database.EnsureCreated(); 

            FileHandler fileHandler = new FileHandler();
            VideoHandler videoHandler = new VideoHandler(fileHandler);
            ImageHandler imageHandler = new ImageHandler();

            List<VideoInfo> mediaList = await videoHandler.getVideoMetadata(db);
            
            RecommendationHandler recommendationHandler = new RecommendationHandler();
            Console.WriteLine("Marking all invalid or too-short videos for removal..."); 
            // We do this before making screenshots so these will be skipped
            mediaList = recommendationHandler.removingShortVideos(mediaList, fileHandler.settings.minVideoLength); 

            await videoHandler.generateAllScreenshotsAsync(mediaList, imageHandler, db);

            Console.WriteLine("Performing checksum validation...");
            mediaList = recommendationHandler.checkCheckSumofAllSimilarVideos(mediaList);

            Console.WriteLine("Generating batch-file...");
            fileHandler.generateBatchFile(mediaList);

            Console.WriteLine("Generating HTML report...");
            fileHandler.generateReport(mediaList);

            Console.WriteLine("Done!");
        }
    }
}

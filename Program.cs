﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
// Publish single trimmed executable: 	
// dotnet publish -r win-x64 -c Release /p:PublishSingleFile=true /p:PublishTrimmed=true

namespace deepduplicates
{
    class Program
    {
        // Accuracy tested to 99.99% with default settings - so check the report for false positives. On average there is aprox. 1 i every 10000 files.
        static async Task Main(string[] args)
        {
            VideoInfoContext db = new VideoInfoContext();
            db.Database.EnsureCreated(); 

            FileHandler fileHandler = await FileHandler.CreateInstance();
            if (fileHandler.firstRun) return;
            ImageHandler imageHandler = new ImageHandler();

            VideoHandler videoHandler = new VideoHandler(fileHandler, imageHandler);
            List<VideoInfo> mediaList = await videoHandler.saveVideoMetadataAndScreenshots(db);

            if (args.Length>0 && args[0].ToLower() == "clean") {
                Console.WriteLine("Clean-mode enabled - DB entries and images are truncated based on current filesystem!");
                CleanupHandler ch = new CleanupHandler(db, fileHandler);
                await ch.runCleanup();
            }

            if (args.Length>0 && args[0].ToLower() == "index") {
                Console.WriteLine("Indexing enabled - complete index of all files is created!");
                fileHandler.generateIndexReport(mediaList);
            } 

            RecommendationHandler recommendationHandler = new RecommendationHandler();
            Console.WriteLine("Marking incomplete videos for removal...");
            mediaList = recommendationHandler.removingIncompleteVideos(mediaList); 

            Console.WriteLine("Performing checksum validation...");
            mediaList = recommendationHandler.checkCheckSumofAllSimilarVideos(mediaList, fileHandler.settings);

            Console.WriteLine("Generating batch-file...");
            fileHandler.generateBatchFile(mediaList);

            Console.WriteLine("Generating HTML report...");
            fileHandler.generateReport(mediaList);

            Console.WriteLine("Generating Encoding batch...");
            fileHandler.generateEncoding(mediaList);

            Console.WriteLine("Check ./output directory");
            Console.WriteLine("Done!");
        }
    }
}

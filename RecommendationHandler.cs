using System;
using System.Collections.Generic;
using System.Linq;

namespace deepduplicates
{
    public class RecommendationHandler
    {
        public List<VideoInfo> removingShortVideos(List<VideoInfo> mediaList, int minVideoLength){
            foreach (VideoInfo item in mediaList)
            {
                if (item.duration <= minVideoLength)
                {
                    item.remove = true;
                    item.reason = "Duration " +minVideoLength+ " sec or less";
                }
                if (item.duration == -1) item.reason = "Can't read videofile";
            }    
            return(mediaList);
        }

        public List<VideoInfo> checkCheckSumofAllSimilarVideos(List<VideoInfo> mediaList){
            // Make delete recommendataions
            List<int?> lengthDubes = mediaList.Where(x => !x.remove).GroupBy(x => x.duration).Where(g => g.Count() > 1).Select(y => y.Key).ToList();
            foreach (int dupeKey in lengthDubes)
            {
                List<VideoInfo> dupGroup = mediaList.Where(p => p.duration == dupeKey).OrderBy(p => p.fileSize).ToList();
                int dupeCount = dupGroup.Count();
                for (int i = 0; i < dupeCount - 1; i++) // from first to sencound-last
                {
                    if (!dupGroup[i].remove)
                    {
                        for (int n = i + 1; n < dupeCount; n++) // from 2nd to last
                        {
                            //Console.WriteLine(dupGroup[i].path + " -> " + dupGroup[n].path);
                            double diff1 = imageChecksumDiff(dupGroup[i].image1Checksum, dupGroup[n].image1Checksum);
                            double diff2 = imageChecksumDiff(dupGroup[i].image2Checksum, dupGroup[n].image2Checksum);
                            double diff1hash = CompareImageHash(dupGroup[i].image1hash, dupGroup[n].image1hash);
                            double diff2hash = CompareImageHash(dupGroup[i].image2hash, dupGroup[n].image2hash);

                            //Console.WriteLine("imageChecksumDiff1:" + diff1 + "  " + "imageChecksumDiff2:" + diff2);
                            if (diff1 < 2.2 && diff2 < 2.2 && diff1hash > 80 && diff2hash > 80)
                            {
                                double confidence = 100 - ((diff1 + diff2) / 2);
                                dupGroup[n].remove = true;
                                dupGroup[n].reason = "Matching length and 2 screenshots have a difference of " + diff1 + "% and " + diff2 + "% - I'm " + confidence + "% confident this is a dupe.";
                                dupGroup[n].triggerId = dupGroup[i].id;
                            }
                        }
                    }
                }
            } 
            return(mediaList);
        }

        public double imageChecksumDiff(long? checksum1, long? checksum2)
        {
            try
            {
                if (checksum1 == null || checksum2 == null ) return(100);

                return (Math.Round(Math.Abs((double)(checksum1 - checksum2) / (double)((checksum1 + checksum2) / 2)) * 100, 2));
            }
            catch
            {
                return (100);
            }
        }

        public double CompareImageHash(List<bool> iHash1, List<bool> iHash2)
        {
            return ((((double)iHash1.Zip(iHash2, (i, j) => i == j).Count(eq => eq)) / 256) * 100);
        }             
    }
}
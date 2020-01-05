using System;
using System.Collections.Generic;
using System.Linq;

namespace deepduplicates
{
    public class RecommendationHandler
    {
        public List<VideoInfo> removingShortVideos(List<VideoInfo> mediaList, int minVideoLength)
        {
            foreach (VideoInfo item in mediaList)
            {
                removingShortVideo(item, minVideoLength);
            }
            return (mediaList);
        }

        public List<VideoInfo> removingIncompleteVideos(List<VideoInfo> mediaList)
        {
            foreach (VideoInfo item in mediaList)
            {
                if((item.image1Checksum == null || item.image2Checksum == null || item.image2Checksum == null) && !item.formatNotSupported){
                    item.remove = true;
                };
                if((item.image1Checksum != null) && (item.image2Checksum == null || item.image2Checksum == null)){

                    item.reason += " - Video is likely incomplete!";
                    item.remove = true;
                };
            }
            return (mediaList);
        }

        public static VideoInfo removingShortVideo(VideoInfo item, int minVideoLength)
        {
            if (item.duration <= minVideoLength)
            {
                item.remove = true;
                item.reason = "Duration " + minVideoLength + " sec or less";
            }
            if (item.duration == -1) item.reason = "Can't read videofile";
            return (item);
        }

        public List<VideoInfo> checkCheckSumofAllSimilarVideos(List<VideoInfo> mediaList)
        {
            // Make delete recommendataions
            List<int?> lengthDubes = mediaList.Where(x => !(x.remove ?? false)).GroupBy(x => x.duration).Where(g => g.Count() > 1).Select(y => y.Key).ToList();
            foreach (int dupeKey in lengthDubes)
            {
                List<VideoInfo> dupGroup = mediaList.Where(p => p.duration == dupeKey && !(p.remove ?? false)).OrderBy(p => p.fileSize).ToList();
                int dupeCount = dupGroup.Count();
                for (int i = 0; i < dupeCount - 1; i++) // from first to secound-last
                {
                    for (int n = i + 1; n < dupeCount; n++) // from 2nd to last
                    {
                        if (dupGroup[i].image1hash == null ||
                        dupGroup[i].image2hash == null ||
                        dupGroup[i].image3hash == null)
                        {
                            continue;
                        }

                        if (dupGroup[n].image1hash == null ||
                            dupGroup[n].image2hash == null ||
                             dupGroup[n].image3hash == null)
                        {
                            continue;
                        }

                        double diff1 = imageChecksumDiff(dupGroup[i].image1Checksum, dupGroup[n].image1Checksum);
                        double diff2 = imageChecksumDiff(dupGroup[i].image2Checksum, dupGroup[n].image2Checksum);
                        double diff3 = imageChecksumDiff(dupGroup[i].image3Checksum, dupGroup[n].image3Checksum);
                        double diff1hash = CompareImageHash(dupGroup[i].image1hash, dupGroup[n].image1hash);
                        double diff2hash = CompareImageHash(dupGroup[i].image2hash, dupGroup[n].image2hash);
                        double diff3hash = CompareImageHash(dupGroup[i].image3hash, dupGroup[n].image3hash);

                        if (diff1 < 2.3 && diff2 < 2.3 && diff3 < 2.3 && diff1hash > 82 && diff2hash > 82 && diff3hash > 82)
                        {
                            double confidence = 100 - ((diff1 + diff2 + diff3) / 3);
                            dupGroup[n].remove = true;
                            dupGroup[n].reason = "Matching length and 2 screenshots have a color difference of " + diff1 + "%, " + diff2 + "% and " + diff3 + "%  Diffhash of: " + diff1hash + ",  "+ diff2hash + " and  "+ diff3hash + ". - Based on color I'm " + confidence + "% confident this is a dupe.";
                            dupGroup[n].triggerId = dupGroup[i].id;
                        }
                    }

                }
            }
            return (mediaList);
        }

        public double imageChecksumDiff(long? checksum1, long? checksum2)
        {
            try
            {
                if (checksum1 == null || checksum2 == null) return (100);

                return (Math.Round(Math.Abs((double)(checksum1 - checksum2) / (double)((checksum1 + checksum2) / 2)) * 100, 2));
            }
            catch
            {
                return (100);
            }
        }

        public double CompareImageHash(List<bool> iHash1, List<bool> iHash2)
        {
            //if(iHash1.Count() != 256 || iHash1.Count() != iHash2.Count()) return(0);
            return ((((double)iHash1.Zip(iHash2, (i, j) => i == j).Count(eq => eq)) / 256) * 100);
        }
    }
}
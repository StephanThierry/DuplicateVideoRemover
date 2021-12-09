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
                if ((item.image1Checksum == null || item.image2Checksum == null || item.image2Checksum == null) && !item.formatNotSupported)
                {
                    item.remove = true;
                };
                if ((item.image1Checksum != null) && (item.image2Checksum == null || item.image2Checksum == null))
                {

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

        public List<VideoInfo> checkCheckSumofAllSimilarVideos(List<VideoInfo> mediaList, Settings settings)
        {
            // Make delete recommendataions duration is divided by 3 and rounded to nearest int so duration does not have to be hard equal 
            List<int?> lengthDubes = mediaList.Where(x => !(x.remove ?? false)).GroupBy(x => x.duration/2).Where(g => g.Count() > 1).Select(y => y.Key).ToList();
            foreach (int dupeKey in lengthDubes)
            {
                List<VideoInfo> dupGroup = mediaList.Where(p => p.path != null && p.path.Length > 1 && p.duration/2 == dupeKey && !(p.remove ?? false)).OrderByDescending(p => settings.priorityFolders.Any(x => p.path.StartsWith(x))).ThenBy(p => p.fileSize).ToList();
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
                        rgb[] diff = new rgb[3];
                        double[] diffHash = new double[3];

                        diff[0] = imageChecksumDiff(dupGroup[i].image1Checksum, dupGroup[n].image1Checksum);
                        diff[1] = imageChecksumDiff(dupGroup[i].image2Checksum, dupGroup[n].image2Checksum);
                        diff[2] = imageChecksumDiff(dupGroup[i].image3Checksum, dupGroup[n].image3Checksum);
                        diffHash[0] = CompareImageHash(dupGroup[i].image1hash, dupGroup[n].image1hash);
                        diffHash[1] = CompareImageHash(dupGroup[i].image2hash, dupGroup[n].image2hash);
                        diffHash[2] = CompareImageHash(dupGroup[i].image3hash, dupGroup[n].image3hash);

                        int score = 0;
                        for(int count=0;count<3;count++){
                            if (diff[count].r < settings.matchSettings.colorTolerance && 
                                diff[count].g < settings.matchSettings.colorTolerance && 
                                diff[count].b < settings.matchSettings.colorTolerance) score ++;
                            if (diffHash[count] > settings.matchSettings.shapeMatch) score ++;
                        }

                        if (score >= 6 - settings.matchSettings.faultTolerance) // 6 = all elements match
                        {
                            dupGroup[n].remove = true;
                            dupGroup[n].reason = $"Matching length. Screenshots have a color difference of {diff[0].r},{diff[0].g},{diff[0].b} : {diff[1].r},{diff[1].g},{diff[1].b} : {diff[2].r},{diff[2].g},{diff[2].b} -   Diffhash of: " + Math.Round(diffHash[0], 1) + ",  " + Math.Round(diffHash[1], 1) + " and  " + Math.Round(diffHash[2], 1) + ".";
                            dupGroup[n].triggerId = dupGroup[i].id;
                        }
                    }

                }

                if (dupGroup.Any(p => p.remove ?? false))
                {
                    foreach (switchPrioritySet switchSet in settings.switchPriority)
                    {
                            foreach(VideoInfo item in dupGroup.Where(p=>p.remove ?? false && p.path.IndexOf(switchSet.up)!=-1)){
                                VideoInfo main = dupGroup.Where(p=>p.id == item.triggerId).First();
                                double fileSizeDiff = Math.Round((double)(Math.Abs((double)(main.fileSize ?? 0)-(double)(item.fileSize ?? 0)) / (double)(main.fileSize)) * 100);
                                if (main.path.IndexOf(switchSet.down) != -1 && item.path.IndexOf(switchSet.up)!= -1 && fileSizeDiff < switchSet.triggerBelowPctSizeDiff) {
                                    main.remove = true;
                                    main.triggerId = item.id;
                                    main.reason = "SWITCHED: "+ switchSet.up + "/" + switchSet.down + "  Sizediff: " + fileSizeDiff + " " + item.reason;
                                    
                                    item.remove = false;
                                    break;
                                }
                            }

                    }
                }
            }
            return (mediaList);
        }

        public rgb imageChecksumDiff(rgb[] checksum1, rgb[] checksum2)
        {
            rgb checksumTotal = new rgb();

            try
            {
                if (checksum1 == null || checksum2 == null) return (checksumTotal);
                for(int i=0; i<checksum1.Length; i++){
                    checksumTotal.r += (int) deviation(checksum1[i].r, checksum2[i].r);
                    checksumTotal.g += (int) deviation(checksum1[i].g, checksum2[i].g);
                    checksumTotal.b += (int) deviation(checksum1[i].b, checksum2[i].b);
                }

                return (checksumTotal);
            }
            catch
            {
                return (checksumTotal);
            }
        }

        private double deviation(int a, int b){
            return((Math.Round(Math.Abs((double)(a - b) / (double)((a + b) / 2)) * 100, 2)));
        }

        public double CompareImageHash(List<bool> iHash1, List<bool> iHash2)
        {
            //if(iHash1.Count() != 256 || iHash1.Count() != iHash2.Count()) return(0);
            return ((((double)iHash1.Zip(iHash2, (i, j) => i == j).Count(eq => eq)) / 256) * 100);
        }
    }
}
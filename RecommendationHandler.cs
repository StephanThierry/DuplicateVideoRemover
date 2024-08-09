using System;
using System.Collections.Generic;
using System.Linq;

namespace deepduplicates
{
    public class RecommendationHandler
    {
        public List<VideoInfo> removingShortVideos(List<VideoInfo> mediaList, int minVideoLength, int minVideoSizeKb)
        {
            foreach (VideoInfo item in mediaList)
            {
                removingShortVideo(item, minVideoLength, minVideoSizeKb);
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

        public static VideoInfo removingShortVideo(VideoInfo item, int minVideoLength, int minVideoSizeKb)
        {
            if (item.duration <= minVideoLength)
            {
                item.remove = true;
                item.reason = "Duration " + minVideoLength + " sec or less";
            }
            if (item.duration == -1) item.reason = "Can't read videofile";
            if (minVideoSizeKb>0 && item.fileSize < minVideoSizeKb) item.reason = "Filesize less than " + minVideoSizeKb + " Kb";
            return (item);
        }

        public class dubeObject{
            public int? duration;
            public int count;
        }        

        public List<VideoInfo> checkCheckSumofAllSimilarVideos(List<VideoInfo> mediaList, Settings settings)
        {
            List<dubeObject> lengthGroups = mediaList.Where(x => !(x.remove ?? false)).GroupBy(x => x.duration).Where(g => g.Count() >= 1).Select(y => new dubeObject{duration = y.Key, count = y.Count()}).OrderBy(x => x.duration).ToList();
            List<dubeObject> org_lengthGroups = mediaList.Where(x => !(x.remove ?? false)).GroupBy(x => x.duration).Where(g => g.Count() >1).Select(y => new dubeObject{duration = y.Key, count = y.Count()}).OrderBy(x => x.duration).ToList();
            List<int?> lengthDubes = new List<int?>();

            // Add all lengths to check with a +/- 1 sec tolerance
            for(int i=0; i<lengthGroups.Count; i++){
                if (lengthGroups[i].count>1) {
                    lengthDubes.Add(lengthGroups[i].duration); 
                } else {
                    if (lengthGroups[i].count>0 && ( (i>0 && lengthGroups[i-1].duration == lengthGroups[i].duration-1) || (i<lengthGroups.Count-1 && lengthGroups[i+1].duration == lengthGroups[i].duration+1) )){
                        lengthDubes.Add(lengthGroups[i].duration); 
                    }
                }
            }

            rgb[] diff = new rgb[3];
            double[] diffHash = new double[3];
            foreach (int dupeKey in lengthDubes)
            {
                // p.duration +/- 1 sec tolerance
                List<VideoInfo> dupGroup = mediaList.Where(p => p.path != null && p.path.Length > 1 && 
                    (p.duration == dupeKey || p.duration-1 == dupeKey || p.duration+1 == dupeKey) && 
                    !(p.remove ?? false)).OrderByDescending(p => settings.priorityFolders.Any(x => p.path.StartsWith(x))).ThenBy(p => p.fileSize).ToList();
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
                        diff[0] = imageChecksumDiff(dupGroup[i].image1Checksum, dupGroup[n].image1Checksum);
                        diff[1] = imageChecksumDiff(dupGroup[i].image2Checksum, dupGroup[n].image2Checksum);
                        diff[2] = imageChecksumDiff(dupGroup[i].image3Checksum, dupGroup[n].image3Checksum);
                        diffHash[0] = CompareImageHash(dupGroup[i].image1hash, dupGroup[n].image1hash);
                        diffHash[1] = CompareImageHash(dupGroup[i].image2hash, dupGroup[n].image2hash);
                        diffHash[2] = CompareImageHash(dupGroup[i].image3hash, dupGroup[n].image3hash);

                        matchSettingsSet currentMatchset = settings.matchSettings;
                        System.IO.FileInfo dupGroup_orgPath = new System.IO.FileInfo(dupGroup[i].path);
                        System.IO.FileInfo dupGroup_diffPath = new System.IO.FileInfo(dupGroup[n].path);
                        String orgFileName = System.IO.Path.GetFileNameWithoutExtension(dupGroup[i].path);
                        String diffFileName = System.IO.Path.GetFileNameWithoutExtension(dupGroup[n].path);

                        if (dupGroup_orgPath.Directory.Name == dupGroup_diffPath.Directory.Name){
                            currentMatchset = settings.matchSettings_sameFolder;
                        }

                        int score = 0;
                        for(int count=0;count<3;count++){
                            if (diff[count].r < currentMatchset.colorTolerance && 
                                diff[count].g < currentMatchset.colorTolerance && 
                                diff[count].b < currentMatchset.colorTolerance) score ++;
                            if (diffHash[count] > currentMatchset.shapeMatch) score ++;
                        }

                        if (currentMatchset.triggerOnNameMatch){
                            if (orgFileName.StartsWith(diffFileName) || orgFileName.StartsWith(diffFileName)){
                                dupGroup[n].remove = true;
                                dupGroup[n].reason = $"Name match triggered";
                                dupGroup[n].triggerId = dupGroup[i].id;
                            }
                        }

                        if (score >= 6 - currentMatchset.faultTolerance) // 6 = all elements match
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
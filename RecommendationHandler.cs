using System;
using System.Collections.Generic;
using System.Linq;

namespace deepduplicates
{
    public class RecommendationHandler
    {
        /// <summary>Called with the first video of each group of videos of the same length that is compared (a live picture on the Scan page).</summary>
        public Action<VideoInfo> OnVideo { get; set; }

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

/// <param name="progress">Optional: share of the comparing done (0..1), measured in pairs of videos compared</param>
public List<VideoInfo> checkChecksumOfAllSimilarVideos_Optimized(List<VideoInfo> mediaList, Settings settings, Action<double> progress = null)
{
    // 1. PRE-FILTERING & GROUPING:
    var videosToProcess = mediaList.Where(v => !(v.remove ?? false) && v.duration.HasValue && v.path != null).ToList();
    // Each hash as 4 x 64 bits once, so a pair is compared with a few XORs instead of 256 steps
    packedHashes = new Dictionary<VideoInfo, ulong[][]>(ReferenceEqualityComparer.Instance);
    foreach (var v in videosToProcess)
        packedHashes[v] = new[] { PackHash(v.image1hash), PackHash(v.image2hash), PackHash(v.image3hash) };
    var videosByDuration = videosToProcess.ToLookup(v => v.duration);

    // Get a sorted list of unique durations 
    var sortedDurations = videosByDuration.Select(g => g.Key.Value).OrderBy(d => d).ToList();
    
    // Use a HashSet for priority folder lookups 
    var priorityFolderSet = new HashSet<string>(settings.priorityFolders);

    // Work in pairs: a group of n videos of the same length compares about n*n/2 pairs
    long PairsAt(int i)
    {
        long n = videosByDuration[sortedDurations[i]].Count();
        if (i + 1 < sortedDurations.Count && sortedDurations[i + 1] == sortedDurations[i] + 1) n += videosByDuration[sortedDurations[i + 1]].Count();
        return n < 2 ? 0 : n * (n - 1) / 2;
    }
    long totalPairs = 0, pairsDone = 0;
    if (progress != null) for (int i = 0; i < sortedDurations.Count; i++) totalPairs += PairsAt(i);
    var lastReport = System.Diagnostics.Stopwatch.StartNew();

    for (int i = 0; i < sortedDurations.Count; i++)
    {
        int currentDuration = sortedDurations[i];
        
        // Get the videos for the current duration and the next duration (+1 sec tolerance)
        var currentGroup = videosByDuration[currentDuration].ToList();
        var nextGroup = (i + 1 < sortedDurations.Count && sortedDurations[i+1] == currentDuration + 1)
            ? videosByDuration[currentDuration + 1].ToList()
            : new List<VideoInfo>();

        var comparisonGroup = currentGroup.Concat(nextGroup).ToList();
        if (comparisonGroup.Count < 2) continue; 

        // Sort based on priority rules.
        comparisonGroup = comparisonGroup
            .OrderByDescending(p => priorityFolderSet.Any(x => p.path.StartsWith(x)))
            .ThenBy(p => p.fileSize)
            .ToList();

        OnVideo?.Invoke(comparisonGroup[0]);
        CompareVideosInGroup(comparisonGroup, settings);
        if (progress != null)
        {
            pairsDone += PairsAt(i);
            if (lastReport.ElapsedMilliseconds >= 250 && totalPairs > 0) { lastReport.Restart(); progress(Math.Min(pairsDone / (double)totalPairs, 1)); }
        }
    }
    
    // 3. POST-PROCESSING (Switch Priority Logic):
    // Group removed items by their duplicate master (triggerId)
    var duplicatesToRecheck = mediaList
        .Where(v => v.remove == true && v.triggerId != -1)
        .ToLookup(v => v.triggerId);

    if (duplicatesToRecheck.Count > 0)
    {
        // Create a fast way to look up videos by ID
        var videoDictById = mediaList.ToDictionary(v => v.id, v => v);

        foreach (var switchSet in settings.switchPriority)
        {
            foreach (var group in duplicatesToRecheck)
            {
                // The video that was originally kept
                if (!videoDictById.TryGetValue(group.Key, out VideoInfo main)) continue;

                // The videos that were marked as duplicates of 'main'
                foreach (var item in group) 
                {
                    // Check if a switch is warranted based on paths
                    if (main.path.Contains(switchSet.down) && item.path.Contains(switchSet.up))
                    {
                        double fileSizeDiff = Math.Abs((double)(main.fileSize ?? 0) - (double)(item.fileSize ?? 0));
                        double percentDiff = main.fileSize > 0 ? (fileSizeDiff / (double)main.fileSize) * 100 : 100;

                        if (percentDiff < switchSet.triggerBelowPctSizeDiff)
                        {
                            // Perform the switch
                            main.remove = true;
                            main.triggerId = item.id;
                            main.reason = $"SWITCHED: {switchSet.up}/{switchSet.down} | Sizediff: {Math.Round(percentDiff, 1)}% | {item.reason}";
                            
                            item.remove = false;
                            item.reason = ""; // Clear the reason from the newly kept item
                            item.triggerId = -1;
                            break; // Switched, move to the next group
                        }
                    }
                }
            }
        }
    }

    return mediaList;
}

// Helper method to contain the core comparison logic
private void CompareVideosInGroup(List<VideoInfo> group, Settings settings)
{
    // The folder name of each video, worked out once (not for every pair: FileInfo is slow)
    var dirNames = new string[group.Count];
    string DirName(int idx) => dirNames[idx] ??= new System.IO.FileInfo(group[idx].path).Directory.Name;

    for (int i = 0; i < group.Count - 1; i++)
    {
        var video1 = group[i];

        if (video1.remove == true) continue;

        for (int n = i + 1; n < group.Count; n++)
        {
            var video2 = group[n];
            if (video2.remove == true) continue; // Already marked for removal

            // Skip if checksums/hashes are missing
            if (video1.image1hash == null || video1.image2hash == null || video1.image3hash == null ||
                video2.image1hash == null || video2.image2hash == null || video2.image3hash == null)
            {
                continue;
            }

            // Determine which match settings to use
            // (falls back to matchSettings when matchSettings_sameFolder is not set in settings.json)
            var currentMatchset = DirName(i) == DirName(n)
                ? settings.matchSettings_sameFolder ?? settings.matchSettings
                : settings.matchSettings;

            // Name match check
            if (currentMatchset.triggerOnNameMatch)
            {
                string fileName1 = System.IO.Path.GetFileNameWithoutExtension(video1.path);
                string fileName2 = System.IO.Path.GetFileNameWithoutExtension(video2.path);
                if (fileName1.StartsWith(fileName2) || fileName2.StartsWith(fileName1))
                {
                    video2.remove = true;
                    video2.reason = $"Name match triggered with {video1.path}";
                    video2.triggerId = video1.id;
                    continue; // Move to the next video
                }
            }

            // Checksum and hash comparison
            double[] diffHash = new double[3];
            diffHash[0] = HashMatch(video1, video2, 0);
            diffHash[1] = HashMatch(video1, video2, 1);
            diffHash[2] = HashMatch(video1, video2, 2);

            int score = 0;
            for (int k = 0; k < 3; k++)
                if (diffHash[k] > currentMatchset.shapeMatch) score++;

            // The three colour checks can add at most 3: most pairs are rejected here, without the colour work
            if (score + 3 < 6 - currentMatchset.faultTolerance) continue;

            rgb[] diff = new rgb[3];
            diff[0] = imageChecksumDiff(video1.image1Checksum, video2.image1Checksum);
            diff[1] = imageChecksumDiff(video1.image2Checksum, video2.image2Checksum);
            diff[2] = imageChecksumDiff(video1.image3Checksum, video2.image3Checksum);
            for (int k = 0; k < 3; k++)
                if (diff[k].r < currentMatchset.colorTolerance && diff[k].g < currentMatchset.colorTolerance && diff[k].b < currentMatchset.colorTolerance) score++;

            if (score >= 6 - currentMatchset.faultTolerance)
            {
                video2.remove = true;
                video2.reason = $"Match with {video1.path}. Score: {score}/6. C-Diff: {diff[0]},{diff[1]},{diff[2]}. H-Match: {Math.Round(diffHash[0])}%,{Math.Round(diffHash[1])}%,{Math.Round(diffHash[2])}%";
                video2.triggerId = video1.id;
            }
        }
    }
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

        private Dictionary<VideoInfo, ulong[][]> packedHashes;

        // The bits in 64-bit words, the number of bits in the last word
        private static ulong[] PackHash(List<bool> h)
        {
            if (h == null) return null;
            var r = new ulong[(h.Count + 63) / 64 + 1];
            for (int i = 0; i < h.Count; i++) if (h[i]) r[i >> 6] |= 1UL << (i & 63);
            r[^1] = (ulong)h.Count;
            return r;
        }

        /// <summary>Share of equal bits (0-100) of the n'th screenshot hash of two videos.</summary>
        private double HashMatch(VideoInfo a, VideoInfo b, int n)
        {
            if (packedHashes != null && packedHashes.TryGetValue(a, out var pa) && packedHashes.TryGetValue(b, out var pb) && pa[n] != null && pb[n] != null && pa[n].Length == pb[n].Length && pa[n][^1] == pb[n][^1])
            {
                // Same as counting the equal bits one by one (and dividing by 256, as before)
                int different = 0;
                var x = pa[n]; var y = pb[n];
                for (int k = 0; k < x.Length - 1; k++) different += System.Numerics.BitOperations.PopCount(x[k] ^ y[k]);
                return ((int)x[^1] - different) / 256.0 * 100;
            }
            return n == 0 ? CompareImageHash(a.image1hash, b.image1hash) : n == 1 ? CompareImageHash(a.image2hash, b.image2hash) : CompareImageHash(a.image3hash, b.image3hash);
        }

        public double CompareImageHash(List<bool> iHash1, List<bool> iHash2)
        {
            //if(iHash1.Count() != 256 || iHash1.Count() != iHash2.Count()) return(0);
            int equal = 0, len = Math.Min(iHash1.Count, iHash2.Count);
            for (int k = 0; k < len; k++) if (iHash1[k] == iHash2[k]) equal++;
            return (equal / 256.0) * 100;
        }
    }
}
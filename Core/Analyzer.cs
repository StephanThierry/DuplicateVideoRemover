using System;
using System.Collections.Generic;
using System.Linq;

namespace deepduplicates
{
    /// <summary>
    /// Runs the matching rules on videos that are already read (from a scan or from the database).
    /// Works on copies, so it can run again and again with different settings - this is what makes
    /// the live preview in the settings screens possible without rescanning.
    /// </summary>
    public static class Analyzer
    {
        public static (ResultSet results, List<VideoInfo> analyzed) Analyze(IReadOnlyList<VideoInfo> media, Settings settings)
        {
            var list = media.Select(Baseline).ToList();
            var rh = new RecommendationHandler();
            rh.removingShortVideos(list, settings.minVideoLength, settings.minVideoSizeKb);
            rh.removingIncompleteVideos(list);
            rh.checkChecksumOfAllSimilarVideos_Optimized(list, settings);
            return (BuildResults(list), list);
        }

        /// <summary>A copy with the matching state cleared, keeping the reason only when reading the file failed.</summary>
        private static VideoInfo Baseline(VideoInfo v)
        {
            var c = v.ShallowCopy();
            c.remove = false;
            c.triggerId = 0;
            bool readFailed = c.duration == -1 || c.image1Checksum == null || c.formatNotSupported;
            c.reason = readFailed ? v.reason : null;
            return c;
        }

        private static ResultSet BuildResults(List<VideoInfo> list)
        {
            var byId = new Dictionary<int, VideoInfo>();
            foreach (var v in list) if (v.id != 0) byId[v.id] = v;

            // Follow "duplicate of" links to the copy that is kept (the switch-priority rule can create chains)
            VideoInfo Root(VideoInfo v)
            {
                var current = v;
                for (int guard = 0; guard < 20; guard++)
                {
                    if (!byId.TryGetValue(current.triggerId, out var next)) return null;
                    if (next.remove != true) return next;
                    current = next;
                }
                return null;
            }

            var result = new ResultSet { VideosInScope = list.Count };
            var groups = new Dictionary<int, DuplicateGroup>();
            foreach (var v in list.Where(v => v.remove == true && !string.IsNullOrEmpty(v.path)))
            {
                var root = v.triggerId > 0 ? Root(v) : null;
                if (root == null)
                {
                    result.Broken.Add(VideoEntry.From(v));
                    continue;
                }
                if (!groups.TryGetValue(root.id, out var group))
                {
                    group = new DuplicateGroup { Id = root.id, SuggestedKeepId = root.id };
                    group.Members.Add(VideoEntry.From(root));
                    groups[root.id] = group;
                }
                group.Members.Add(VideoEntry.From(v));
            }
            // Biggest savings first
            result.Groups = groups.Values
                .OrderByDescending(g => g.Members.Skip(1).Sum(m => m.SizeBytes))
                .ToList();
            result.Broken = result.Broken.OrderBy(b => b.Path, StringComparer.OrdinalIgnoreCase).ToList();
            return result;
        }
    }
}

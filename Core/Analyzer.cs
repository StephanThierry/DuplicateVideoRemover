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
        /// <param name="progress">Optional: share of the comparing done (0..1)</param>
        public static (ResultSet results, List<VideoInfo> analyzed) Analyze(IReadOnlyList<VideoInfo> media, Settings settings, Action<double> progress = null)
        {
            var list = media.Select(Baseline).ToList();
            var rh = new RecommendationHandler();
            rh.removingShortVideos(list, settings.minVideoLength, settings.minVideoSizeKb);
            rh.removingIncompleteVideos(list);
            rh.checkChecksumOfAllSimilarVideos_Optimized(list, settings, progress);
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
            if (FalsePositives.Count > 0)
                foreach (var g in groups.Values.ToList())
                    if (!DropFalsePositives(g, byId)) groups.Remove(g.Id);
            // Biggest savings first
            result.Groups = groups.Values
                .OrderByDescending(g => g.Members.Skip(1).Sum(m => m.SizeBytes))
                .ToList();
            result.Broken = result.Broken.OrderBy(b => b.Path, StringComparer.OrdinalIgnoreCase).ToList();
            return result;
        }

        /// <summary>
        /// Takes out the copies marked as "not a duplicate" (see FalsePositives): a copy marked against the
        /// kept one, or a kept one marked against all the others. The ones taken out count as normal videos.
        /// Returns false when fewer than two copies are left.
        /// </summary>
        private static bool DropFalsePositives(DuplicateGroup g, Dictionary<int, VideoInfo> byId)
        {
            var prints = new Dictionary<int, string>();
            string Print(int id) => prints.TryGetValue(id, out var p) ? p : prints[id] = byId.TryGetValue(id, out var v) ? FalsePositives.Fingerprint(v) : null;
            void Release(VideoEntry e)
            {
                g.Members.Remove(e);
                if (byId.TryGetValue(e.Id, out var v)) { v.remove = false; v.triggerId = 0; v.reason = null; }
            }

            bool changed = true;
            while (changed && g.Members.Count >= 2)
            {
                changed = false;
                var root = g.Members[0];
                foreach (var m in g.Members.Skip(1).ToList())
                    if (FalsePositives.IsPair(Print(m.Id), Print(root.Id))) { Release(m); changed = true; }
                if (g.Members.Count >= 2 && g.Members.Skip(1).All(m => FalsePositives.IsPair(Print(root.Id), Print(m.Id))))
                {
                    // The kept copy was marked as not a duplicate of the rest: keep the next one instead
                    Release(root);
                    if (byId.TryGetValue(g.Members[0].Id, out var next)) { next.remove = false; next.reason = null; }
                    g.SuggestedKeepId = g.Members[0].Id;
                    changed = true;
                }
            }
            if (g.Members.Count >= 2) return true;
            foreach (var m in g.Members.ToList()) Release(m);
            return false;
        }
    }
}

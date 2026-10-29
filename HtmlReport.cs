using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace deepduplicates
{
    /// <summary>
    /// Writes the HTML reports (report.html and indexReport.html) as single, self-contained pages
    /// with a readable layout, light/dark mode, and search/filter.
    /// Screenshots are linked with relative paths (../_screens/...), so the reports work on
    /// Windows and Linux and when the app folder is moved.
    /// </summary>
    public class HtmlReport
    {
        private readonly FileHandler fileHandler;

        public HtmlReport(FileHandler fileHandler)
        {
            this.fileHandler = fileHandler;
        }

        public void WriteDeleteReport(List<VideoInfo> mediaList, string outputPath)
        {
            var byId = mediaList.Where(v => v.id != 0).GroupBy(v => v.id).ToDictionary(g => g.Key, g => g.First());
            var toRemove = mediaList.Where(x => x.remove ?? false).ToList();

            // Duplicates grouped under the original that is kept
            var groups = toRemove
                .Where(x => byId.ContainsKey(x.triggerId))
                .GroupBy(x => x.triggerId)
                .Select(g => (original: byId[g.Key], dupes: g.OrderByDescending(d => d.fileSize ?? 0).ToList()))
                .OrderByDescending(g => g.dupes.Sum(d => d.fileSize ?? 0))
                .ToList();

            // Broken, incomplete, too short or otherwise flagged without an original
            var other = toRemove.Where(x => !byId.ContainsKey(x.triggerId)).OrderBy(x => x.path).ToList();

            long dupeBytes = groups.Sum(g => g.dupes.Sum(d => d.fileSize ?? 0));
            long otherBytes = other.Sum(x => x.fileSize ?? 0);
            long originalBytes = groups.Sum(g => g.original.fileSize ?? 0);
            int dupeCount = groups.Sum(g => g.dupes.Count);

            var html = new StringBuilder();
            WriteHead(html, "Duplicate report");
            html.Append("<header class=\"top\"><div class=\"wrap\">");
            html.Append("<h1>Duplicate report</h1>");
            html.Append($"<p class=\"sub\">Generated {Enc(DateTime.Now.ToString("yyyy-MM-dd HH:mm"))} on {Enc(Environment.MachineName)} · Review before running <code>delete_all_dupes{ScriptFile.Extension}</code></p>");

            html.Append("<div class=\"stats\">");
            Stat(html, FormatSize(dupeBytes + otherBytes), "can be freed", "accent");
            Stat(html, (dupeCount + other.Count).ToString("N0"), "files to delete");
            Stat(html, dupeCount.ToString("N0"), $"duplicates of {Plural(groups.Count, "original", "originals")}");
            Stat(html, other.Count.ToString("N0"), "broken, incomplete or short");
            Stat(html, FormatSize(originalBytes), "kept originals");
            html.Append("</div>");

            WriteToolbar(html, true);
            html.Append("</div></header><main class=\"wrap\">");

            if (groups.Count == 0 && other.Count == 0)
                html.Append("<p class=\"empty\">Nothing to delete. No duplicates or broken videos were found.</p>");

            if (groups.Count > 0)
            {
                html.Append($"<h2 class=\"section\" data-section=\"dupes\">Duplicates <span>{Plural(groups.Count, "group", "groups")}</span></h2>");
                foreach (var (original, dupes) in groups)
                {
                    html.Append("<article class=\"card\" data-kind=\"dupes\">");
                    VideoRow(html, original, "keep", "Keep", null);
                    foreach (var dupe in dupes) VideoRow(html, dupe, "delete", "Delete", dupe.reason);
                    html.Append("</article>");
                }
            }

            if (other.Count > 0)
            {
                html.Append($"<h2 class=\"section\" data-section=\"broken\">Broken, incomplete or too short <span>{Plural(other.Count, "file", "files")}</span></h2>");
                foreach (var item in other)
                {
                    html.Append("<article class=\"card\" data-kind=\"broken\">");
                    string note = item.triggerId > 0 ? "Original not found - check the reason. " : "";
                    VideoRow(html, item, "delete", "Delete", note + item.reason);
                    html.Append("</article>");
                }
            }

            html.Append("<p class=\"none hidden\" id=\"none\">No videos match the filter.</p>");
            html.Append("</main>");
            WriteScript(html);
            html.Append("</body></html>");
            File.WriteAllText(outputPath, html.ToString(), new UTF8Encoding(false));
        }

        public void WriteIndexReport(List<VideoInfo> mediaList, string outputPath)
        {
            var items = mediaList.Where(v => !string.IsNullOrEmpty(v.path)).OrderBy(p => p.path).ToList();
            long totalBytes = items.Sum(v => v.fileSize ?? 0);

            var html = new StringBuilder();
            WriteHead(html, "Video index");
            html.Append("<header class=\"top\"><div class=\"wrap\">");
            html.Append("<h1>Video index</h1>");
            html.Append($"<p class=\"sub\">Generated {Enc(DateTime.Now.ToString("yyyy-MM-dd HH:mm"))} on {Enc(Environment.MachineName)}</p>");
            html.Append("<div class=\"stats\">");
            Stat(html, items.Count.ToString("N0"), "videos", "accent");
            Stat(html, FormatSize(totalBytes), "total size");
            Stat(html, FormatDuration(items.Sum(v => Math.Max(v.duration ?? 0, 0))), "total duration");
            html.Append("</div>");
            WriteToolbar(html, false);
            html.Append("</div></header><main class=\"wrap\">");

            foreach (var item in items)
            {
                html.Append("<article class=\"card\" data-kind=\"index\">");
                VideoRow(html, item, null, null, null);
                html.Append("</article>");
            }

            html.Append("<p class=\"none hidden\" id=\"none\">No videos match the filter.</p>");
            html.Append("</main>");
            WriteScript(html);
            html.Append("</body></html>");
            File.WriteAllText(outputPath, html.ToString(), new UTF8Encoding(false));
        }

        // --- Building blocks ---

        private void VideoRow(StringBuilder html, VideoInfo item, string kind, string label, string reason)
        {
            string path = item.path ?? "";
            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileName(path);
            if (dir.Length > 0) dir += Path.DirectorySeparatorChar;

            html.Append($"<div class=\"row {kind}\" data-search=\"{Enc(path.ToLowerInvariant())}\">");
            html.Append("<div class=\"thumbs\">");
            for (int i = 1; i <= 3; i++)
            {
                html.Append($"<img loading=\"lazy\" src=\"{Enc(ScreenshotUrl(item, i))}\" alt=\"\" onerror=\"this.onerror=null;this.src='{BlankImage}';this.classList.add('missing')\">");
            }
            html.Append("</div><div class=\"info\">");
            if (label != null) html.Append($"<span class=\"badge {kind}\">{label}</span>");
            html.Append($"<div class=\"path\" title=\"Click to copy\" data-copy=\"{Enc(path)}\"><span class=\"dir\">{Enc(dir)}</span><span class=\"name\">{Enc(name)}</span></div>");

            html.Append("<div class=\"meta\">");
            if (item.fileSize is long size && size >= 0) html.Append($"<span>{FormatSize(size)}</span>");
            if (item.duration is int d && d >= 0) html.Append($"<span>{FormatDuration(d)}</span>");
            if (item.bitrate is double b && b > 0) html.Append($"<span>{Math.Round(b / 1000):N0} kbps</span>");
            html.Append("</div>");

            if (!string.IsNullOrWhiteSpace(reason))
            {
                // Reasons may contain an HTML comment with the raw error - show it as a tooltip instead
                string detail = string.Join(" ", Regex.Matches(reason, "<!--(.*?)-->", RegexOptions.Singleline).Select(m => m.Groups[1].Value.Trim()));
                string text = Regex.Replace(reason, "<!--.*?-->", "", RegexOptions.Singleline).Trim(' ', '-');
                html.Append($"<div class=\"reason\"{(detail.Length > 0 ? $" title=\"{Enc(detail)}\"" : "")}>{Enc(text)}</div>");
            }
            html.Append("</div></div>");
        }

        private static void Stat(StringBuilder html, string value, string label, string cls = "")
        {
            html.Append($"<div class=\"stat {cls}\"><b>{Enc(value)}</b><span>{Enc(label)}</span></div>");
        }

        private static void WriteToolbar(StringBuilder html, bool withFilters)
        {
            html.Append("<div class=\"toolbar\"><input id=\"q\" type=\"search\" placeholder=\"Filter by path or file name…\" autocomplete=\"off\">");
            if (withFilters)
            {
                html.Append("<div class=\"chips\" role=\"group\">");
                html.Append("<button class=\"chip on\" data-filter=\"all\">All</button>");
                html.Append("<button class=\"chip\" data-filter=\"dupes\">Duplicates</button>");
                html.Append("<button class=\"chip\" data-filter=\"broken\">Broken</button>");
                html.Append("</div>");
            }
            html.Append("<span id=\"count\" class=\"count\"></span></div>");
        }

        private string ScreenshotUrl(VideoInfo item, int index)
        {
            string relative = Path.GetRelativePath(fileHandler.outputFolder, fileHandler.screenshotPath(item, index));
            return string.Join("/", relative.Split('\\', '/').Select(Uri.EscapeDataString));
        }

        // 1x1 transparent GIF, shown (with a striped background) when a screenshot is missing
        private const string BlankImage = "data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==";

        private static string Plural(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";

        private static string Enc(string s) => WebUtility.HtmlEncode(s ?? "");

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0} MB";
            return $"{Math.Max(bytes, 0) / 1024.0:0} KB";
        }

        public static string FormatDuration(long seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        private static void WriteHead(StringBuilder html, string title)
        {
            html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            html.Append($"<title>{Enc(title)}</title><style>{Css}</style></head><body>");
        }

        private static void WriteScript(StringBuilder html) => html.Append($"<script>{Script}</script>");

        private const string Css = """
:root{--bg:#f5f6f8;--card:#fff;--text:#1d2129;--muted:#6b7280;--line:#e3e6eb;--accent:#2563eb;
--keep:#15803d;--keep-bg:#e8f6ed;--del:#c2410c;--del-bg:#fdeee6;--thumb:#e9ecf1;color-scheme:light}
@media (prefers-color-scheme:dark){:root{--bg:#111318;--card:#1a1d24;--text:#e6e8ec;--muted:#9aa3b2;--line:#2a2f39;
--accent:#6ea0ff;--keep:#4ade80;--keep-bg:#16291e;--del:#fb923c;--del-bg:#2e1d12;--thumb:#232832;color-scheme:dark}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 system-ui,-apple-system,"Segoe UI",Roboto,Ubuntu,sans-serif}
.wrap{max-width:1180px;margin:0 auto;padding:0 20px}
.top{background:var(--card);border-bottom:1px solid var(--line);padding:22px 0 14px;position:sticky;top:0;z-index:5}
h1{margin:0;font-size:22px;letter-spacing:-.01em}
.sub{margin:4px 0 16px;color:var(--muted)}
code{font:12px ui-monospace,SFMono-Regular,Consolas,monospace;background:var(--thumb);padding:1px 5px;border-radius:4px}
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:10px;margin-bottom:14px}
.stat{background:var(--bg);border:1px solid var(--line);border-radius:10px;padding:10px 12px}
.stat b{display:block;font-size:20px;font-variant-numeric:tabular-nums}
.stat span{color:var(--muted);font-size:12px}
.stat.accent b{color:var(--accent)}
.toolbar{display:flex;flex-wrap:wrap;gap:10px;align-items:center}
#q{flex:1 1 260px;padding:8px 12px;border:1px solid var(--line);border-radius:8px;background:var(--bg);color:var(--text);font:inherit}
#q:focus{outline:2px solid var(--accent);outline-offset:-1px}
.chips{display:flex;gap:6px}
.chip{border:1px solid var(--line);background:var(--bg);color:var(--text);padding:6px 12px;border-radius:999px;font:inherit;cursor:pointer}
.chip.on{background:var(--accent);border-color:var(--accent);color:#fff}
.count{color:var(--muted);font-size:12px;margin-left:auto}
.section{font-size:16px;margin:28px 0 10px}
.section span{color:var(--muted);font-weight:400;font-size:13px;margin-left:6px}
.card{background:var(--card);border:1px solid var(--line);border-radius:12px;margin:0 0 12px;overflow:hidden}
.row{display:flex;gap:16px;padding:12px;align-items:flex-start;border-left:4px solid transparent}
.row+.row{border-top:1px solid var(--line)}
.row.keep{border-left-color:var(--keep)}
.row.delete{border-left-color:var(--del)}
.thumbs{display:flex;gap:4px;flex:0 0 auto}
.thumbs img{width:144px;aspect-ratio:3/2;object-fit:cover;border-radius:6px;background:var(--thumb);display:block}
.thumbs img.missing{background:repeating-linear-gradient(45deg,var(--thumb) 0 8px,var(--line) 8px 16px)}
.info{min-width:0;flex:1}
.badge{display:inline-block;font-size:11px;font-weight:600;text-transform:uppercase;letter-spacing:.04em;padding:2px 8px;border-radius:999px;margin-bottom:6px}
.badge.keep{color:var(--keep);background:var(--keep-bg)}
.badge.delete{color:var(--del);background:var(--del-bg)}
.path{word-break:break-all;cursor:copy}
.path .dir{color:var(--muted)}
.path .name{font-weight:600}
.path.copied .name::after{content:"  copied";color:var(--accent);font-weight:400;font-size:12px}
.meta{display:flex;flex-wrap:wrap;gap:14px;color:var(--muted);font-size:12px;margin-top:4px;font-variant-numeric:tabular-nums}
.reason{margin-top:8px;font:12px/1.5 ui-monospace,SFMono-Regular,Consolas,monospace;color:var(--muted);background:var(--bg);border-radius:6px;padding:6px 8px;word-break:break-word}
.hidden{display:none!important}
.empty,.none{color:var(--muted);text-align:center;padding:40px 0}
@media (max-width:760px){.row{flex-direction:column}.thumbs img{width:calc((100vw - 80px)/3)}.top{position:static}}
""";

        private const string Script = """
(function(){
var q=document.getElementById('q'),cards=[].slice.call(document.querySelectorAll('.card')),
sections=[].slice.call(document.querySelectorAll('.section')),none=document.getElementById('none'),
count=document.getElementById('count'),filter='all';
function apply(){var t=q.value.trim().toLowerCase(),shown=0,per={};
cards.forEach(function(c){var kind=c.getAttribute('data-kind');
var ok=(filter==='all'||kind===filter)&&(!t||[].some.call(c.querySelectorAll('.row'),function(r){return r.getAttribute('data-search').indexOf(t)>=0}));
c.classList.toggle('hidden',!ok);if(ok){shown++;per[kind]=1}});
sections.forEach(function(s){s.classList.toggle('hidden',!per[s.getAttribute('data-section')])});
none.classList.toggle('hidden',shown>0||cards.length===0);
count.textContent=shown===cards.length?cards.length+' items':shown+' of '+cards.length+' items'}
q.addEventListener('input',apply);
[].forEach.call(document.querySelectorAll('.chip'),function(b){b.addEventListener('click',function(){
filter=b.getAttribute('data-filter');[].forEach.call(document.querySelectorAll('.chip'),function(x){x.classList.toggle('on',x===b)});apply()})});
document.addEventListener('click',function(e){var p=e.target.closest('.path');if(!p||!navigator.clipboard)return;
navigator.clipboard.writeText(p.getAttribute('data-copy')).then(function(){p.classList.add('copied');setTimeout(function(){p.classList.remove('copied')},1200)})});
apply()})();
""";
    }
}

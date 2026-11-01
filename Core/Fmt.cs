using System;

namespace deepduplicates
{
    /// <summary>Formatting shared by the interfaces.</summary>
    public static class Fmt
    {
        public static string Size(long bytes)
        {
            if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):0.00} TB";
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0} MB";
            return $"{Math.Max(bytes, 0) / 1024.0:0} KB";
        }

        public static string Duration(int seconds) => seconds < 0 ? "unknown length" : Time(TimeSpan.FromSeconds(seconds));

        public static string Time(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

        /// <summary>"about 2 h 10 min", "about 4 min", "less than a minute".</summary>
        public static string Approx(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "less than a minute";
            if (t.TotalHours < 1) return $"about {Math.Ceiling(t.TotalMinutes)} min";
            return $"about {(int)t.TotalHours} h {t.Minutes} min";
        }

        public static string Bitrate(double bitsPerSecond) => bitsPerSecond > 0 ? $"{bitsPerSecond / 1000:N0} kbps" : "";

        public static string Ago(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            if (d.TotalMinutes < 2) return "just now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} minutes ago";
            if (d.TotalDays < 1) return $"{(int)d.TotalHours} hours ago";
            if (d.TotalDays < 2) return "yesterday";
            return $"{(int)d.TotalDays} days ago";
        }

        /// <summary>"today at 20:16", "yesterday at 20:16", "1 Nov at 20:16" (with the year when not this year).</summary>
        public static string When(DateTime utc)
        {
            var t = utc.ToLocalTime();
            var today = DateTime.Now.Date;
            string day = t.Date == today ? "today" : t.Date == today.AddDays(-1) ? "yesterday"
                : t.ToString(t.Year == today.Year ? "d MMM" : "d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
            return $"{day} at {t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}";
        }

        public static string Plural(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";
    }
}

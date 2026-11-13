using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using deepduplicates;

namespace DuplicateVideoRemover.Desktop.ViewModels;

/// <summary>
/// Loads screenshots from _screens, with a small cache. Each is drawn at its video's own shape (the files are all 3:2, see
/// <see cref="VideoShape"/>); one loaded before the shape was known is loaded again once it is.
/// </summary>
public static class Shots
{
    private const int MaxCached = 900;
    private static readonly Dictionary<string, (Bitmap? Bitmap, double Aspect)> cache = new();
    private static readonly Queue<string> order = new();

    public static Bitmap? Get(int videoId, int index)
    {
        string path = Program.Controller.ScreenshotPath(videoId, index);
        double aspect = VideoShapes.Of(videoId);
        lock (cache)
        {
            if (cache.TryGetValue(path, out var hit) && hit.Aspect == aspect) return hit.Bitmap;
        }
        var bitmap = Load(path, aspect);
        lock (cache)
        {
            if (!cache.ContainsKey(path)) order.Enqueue(path);
            cache[path] = (bitmap, aspect);
            while (order.Count > MaxCached) cache.Remove(order.Dequeue());
        }
        return bitmap;
    }

    /// <summary>A screenshot file drawn at the given shape (<see cref="VideoShape.Unknown"/>: as it is); null when it is not there.</summary>
    public static Bitmap? Load(string path, double aspect)
    {
        try
        {
            if (VideoShape.ReadFitted(path, aspect) is not byte[] png) return null;
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch { return null; }   // unreadable screenshot - show nothing
    }
}

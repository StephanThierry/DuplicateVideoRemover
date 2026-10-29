using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;

namespace DuplicateVideoRemover.Desktop.ViewModels;

/// <summary>Loads screenshots from _screens, scaled down, with a small cache.</summary>
public static class Shots
{
    private const int MaxCached = 900;
    private static readonly Dictionary<string, Bitmap?> cache = new();
    private static readonly Queue<string> order = new();

    public static Bitmap? Get(int videoId, int index)
    {
        string path = Program.Controller.ScreenshotPath(videoId, index);
        lock (cache)
        {
            if (cache.TryGetValue(path, out var hit)) return hit;
        }
        Bitmap? bitmap = null;
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                bitmap = Bitmap.DecodeToWidth(stream, 240);
            }
        }
        catch { /* unreadable screenshot - show nothing */ }
        lock (cache)
        {
            cache[path] = bitmap;
            order.Enqueue(path);
            while (order.Count > MaxCached) cache.Remove(order.Dequeue());
        }
        return bitmap;
    }
}

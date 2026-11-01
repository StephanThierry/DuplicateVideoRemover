using System.IO;

namespace deepduplicates
{
    /// <summary>Deleting a video file the user picked.</summary>
    public static class VideoFile
    {
        /// <summary>
        /// Deletes the file. A read-only file is made writable first: .NET refuses to delete read-only files
        /// ("Access to the path is denied") even with full permissions, and files copied from cameras, discs or
        /// backups often have that flag. A file that is already gone counts as deleted.
        /// </summary>
        public static void Delete(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            if (info.IsReadOnly) info.IsReadOnly = false;
            info.Delete();
        }
    }
}

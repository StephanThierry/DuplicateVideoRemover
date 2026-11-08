#nullable enable
using System;
using System.Diagnostics;
using System.IO;

namespace deepduplicates
{
    /// <summary>
    /// Deleting a file the user can still get back. Windows: the Recycle Bin - but a network share (\\nas\video, or a mapped
    /// drive) has none, and neither do most USB drives; the shell deletes such files for good, so
    /// <see cref="IsRecoverable"/> says up front what will happen and the interfaces tell the user before asking.
    /// Linux: the desktop trash through "gio trash" or "trash-put" when one is installed.
    /// </summary>
    public static class RecycleBin
    {
        /// <summary>True when <see cref="Delete"/> puts the file where it can be restored from.</summary>
        public static bool IsRecoverable(string path)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
                    if (root.Length == 0 || root.StartsWith(@"\\")) return false;
                    return new DriveInfo(root).DriveType == DriveType.Fixed;
                }
                return TrashTool() != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Moves the file to the Recycle Bin (trash). When there is none for this file (see <see cref="IsRecoverable"/>)
        /// it is deleted permanently instead. Returns true when it can be restored. A file that is already gone counts as deleted.
        /// </summary>
        public static bool Delete(string path)
        {
            if (!File.Exists(path)) return IsRecoverable(path);
            if (!IsRecoverable(path)) { VideoFile.Delete(path); return false; }

            if (OperatingSystem.IsWindows())
            {
                MoveToWindowsRecycleBin(path);
            }
            else
            {
                var (tool, args) = TrashTool()!.Value;
                var info = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var a in args) info.ArgumentList.Add(a);
                info.ArgumentList.Add("--");
                info.ArgumentList.Add(path);
                using var p = Process.Start(info)!;
                string error = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new IOException($"Could not move the file to the trash: {error.Trim()}");
            }
            if (File.Exists(path)) throw new IOException("The file is still there after moving it to the Recycle Bin.");
            return true;
        }

        // Kept apart so the Windows-only call is not seen on other systems
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void MoveToWindowsRecycleBin(string path)
        {
            // OnlyErrorDialogs: no "are you sure" box (the interface asked already); read-only files are fine here
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
        }

        private static (string tool, string[] args)? TrashTool()
        {
            foreach (var (name, args) in new[] { ("gio", new[] { "trash" }), ("trash-put", Array.Empty<string>()) })
                if (FileHandler.FindOnPath(name) != null) return (name, args);
            return null;
        }
    }
}

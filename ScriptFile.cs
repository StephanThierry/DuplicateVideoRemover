using System;
using System.IO;
using System.Text;

namespace deepduplicates
{
    /// <summary>
    /// Writes a .bat file on Windows and a .sh file on Linux/macOS.
    /// Use the helpers to produce commands and quoted paths for the current OS.
    /// </summary>
    public sealed class ScriptFile : IDisposable
    {
        public static bool IsWindows => OperatingSystem.IsWindows();
        public static string Extension => IsWindows ? ".bat" : ".sh";

        public string FilePath { get; }
        private readonly StreamWriter writer;

        /// <param name="folder">Output folder</param>
        /// <param name="baseName">File name without extension, e.g. "delete_all_dupes"</param>
        /// <param name="changeToScriptFolder">Start the script by changing to its own folder (for relative paths)</param>
        public ScriptFile(string folder, string baseName, bool changeToScriptFolder = false)
        {
            FilePath = Path.Combine(folder, baseName + Extension);
            // No BOM: cmd.exe would try to run it as part of the first command, and sh needs "#!" first
            writer = new StreamWriter(FilePath, false, new UTF8Encoding(false));
            writer.NewLine = IsWindows ? "\r\n" : "\n";

            if (IsWindows)
            {
                writer.WriteLine("chcp 65001"); // UTF-8 code page, so special characters in paths work
                if (changeToScriptFolder) writer.WriteLine("cd /d \"%~dp0\"");
            }
            else
            {
                writer.WriteLine("#!/bin/sh");
                if (changeToScriptFolder) writer.WriteLine("cd \"$(dirname \"$0\")\" || exit 1");
            }
        }

        public void Line(string text = "") => writer.WriteLine(text);

        public void Comment(string text) => writer.WriteLine((IsWindows ? "REM " : "# ") + text.Replace("\r", " ").Replace("\n", " "));

        public void Delete(string path) =>
            writer.WriteLine(IsWindows ? $"DEL {Quote(path)} /f" : $"rm -f -- {Quote(path)}");

        public void Move(string from, string to) =>
            writer.WriteLine(IsWindows ? $"MOVE {Quote(from)} {Quote(to)}" : $"mv -- {Quote(from)} {Quote(to)}");

        public void Rename(string from, string to) =>
            writer.WriteLine(IsWindows ? $"ren {Quote(from)} {Quote(to)}" : $"mv -n -- {Quote(from)} {Quote(to)}");

        /// <summary>Quotes a path for the current shell.</summary>
        public static string Quote(string path)
        {
            // .bat: % must be written as %%.  sh: single quotes keep everything literal; ' becomes '\''
            return IsWindows
                ? "\"" + path.Replace("%", "%%") + "\""
                : "'" + path.Replace("'", "'\\''") + "'";
        }

        public void Dispose()
        {
            writer.Dispose();
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(FilePath, File.GetUnixFileMode(FilePath)
                        | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Could not make {FilePath} executable: {e.Message}");
                }
            }
        }
    }
}

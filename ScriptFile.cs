using System;
using System.IO;
using System.Text;

namespace deepduplicates
{
    /// <summary>
    /// Writes a PowerShell script (.ps1) on Windows and a shell script (.sh) on Linux/macOS.
    ///
    /// PowerShell is used instead of .bat because cmd.exe can't use UNC paths (\\server\share\...)
    /// as the current folder, mangles % in paths and needs code page tricks for special characters.
    /// PowerShell handles UNC paths natively, and -LiteralPath / single quotes keep [ ] $ % ' literal.
    /// </summary>
    public sealed class ScriptFile : IDisposable
    {
        public static bool IsWindows => OperatingSystem.IsWindows();
        public static string Extension => IsWindows ? ".ps1" : ".sh";

        public string FilePath { get; }
        private readonly StreamWriter writer;
        private readonly bool powerShell;

        /// <param name="folder">Output folder</param>
        /// <param name="baseName">File name without extension, e.g. "delete_all_dupes"</param>
        /// <param name="powerShell">Write PowerShell (default on Windows) instead of sh</param>
        public ScriptFile(string folder, string baseName, bool? powerShell = null)
        {
            this.powerShell = powerShell ?? IsWindows;
            FilePath = Path.Combine(folder, baseName + (this.powerShell ? ".ps1" : ".sh"));

            // Windows PowerShell 5.1 reads a .ps1 without BOM as ANSI, breaking æ/ø/å etc. in paths,
            // so .ps1 gets a UTF-8 BOM. sh must start with "#!", so no BOM there.
            writer = new StreamWriter(FilePath, false, new UTF8Encoding(this.powerShell));
            writer.NewLine = this.powerShell ? "\r\n" : "\n";

            if (this.powerShell)
            {
                writer.WriteLine("# Run with:  powershell -ExecutionPolicy Bypass -File \"" + Path.GetFileName(FilePath) + "\"");
                writer.WriteLine("$ErrorActionPreference = 'Continue'");
                writer.WriteLine("Set-Location -LiteralPath $PSScriptRoot  # works for UNC paths too");
            }
            else
            {
                writer.WriteLine("#!/bin/sh");
                writer.WriteLine("cd \"$(dirname \"$0\")\" || exit 1");
            }
            writer.WriteLine();
        }

        public void Line(string text = "") => writer.WriteLine(text);

        public void Comment(string text) => writer.WriteLine("# " + text.Replace("\r", " ").Replace("\n", " "));

        public void Delete(string path) => writer.WriteLine(powerShell
            ? $"Remove-Item -LiteralPath {Quote(path)} -Force"
            : $"rm -f -- {Quote(path)}");

        public void Move(string from, string to) => writer.WriteLine(powerShell
            // .NET method: no wildcard handling of the destination, works with UNC paths
            ? $"[System.IO.File]::Move({Quote(from)}, {Quote(to)})"
            : $"mv -- {Quote(from)} {Quote(to)}");

        public void Rename(string from, string to) => writer.WriteLine(powerShell
            ? $"Rename-Item -LiteralPath {Quote(from)} -NewName {Quote(Path.GetFileName(to.Replace('\\', '/')))}"
            : $"mv -n -- {Quote(from)} {Quote(to)}");

        /// <summary>Runs a program with arguments, each argument quoted.</summary>
        public void Run(string program, params string[] args)
        {
            var line = new StringBuilder(powerShell ? "& " + Quote(program) : Quote(program));
            foreach (string a in args) line.Append(' ').Append(Quote(a ?? ""));
            writer.WriteLine(line.ToString());
        }

        /// <summary>Quotes a value as a literal string for this script's shell.</summary>
        public string Quote(string value)
        {
            // Both shells: single quotes are fully literal (no $, %, [ ] or ` expansion).
            // PowerShell escapes ' as '' - and also treats the typographic quotes ‘ ’ ‚ ‛ as single
            // quotes, so those are doubled too.  sh escapes ' as '\''.
            return powerShell
                ? "'" + System.Text.RegularExpressions.Regex.Replace(value, "['\u2018\u2019\u201A\u201B]", m => m.Value + m.Value) + "'"
                : "'" + value.Replace("'", "'\\''") + "'";
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

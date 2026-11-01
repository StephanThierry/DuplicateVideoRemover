using System;
using System.IO;
using System.Threading;

namespace deepduplicates
{
    public sealed class ScanAlreadyRunningException : Exception
    {
        public ScanAlreadyRunningException(string message) : base(message) { }
    }

    /// <summary>
    /// Makes sure only one scan uses a database at a time, also when the database is shared between
    /// machines (a container and a PC) or programs (web app and command line).
    ///
    /// A file scan.lock next to the database says who is scanning and is touched every 30 seconds.
    /// A lock that has not been touched for 2 minutes is left over from a crash and is taken over.
    /// </summary>
    public sealed class ScanLock : IDisposable
    {
        private static readonly TimeSpan Stale = TimeSpan.FromMinutes(2);
        private readonly string path;
        private readonly string owner;
        private readonly Timer heartbeat;

        private ScanLock(string path, string owner)
        {
            this.path = path;
            this.owner = owner;
            heartbeat = new Timer(_ => Touch(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public static ScanLock Acquire(string program)
        {
            string path = Path.Combine(DataLocation.DatabaseFolder, "scan.lock");
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < Stale)
            {
                string who = "another program";
                try { who = File.ReadAllText(path).Trim(); } catch { }
                throw new ScanAlreadyRunningException(
                    $"A scan is already running ({who}). Wait for it to finish - its results show up here afterwards.");
            }
            string owner = $"{program} on {Environment.MachineName}, started {DateTime.Now:yyyy-MM-dd HH:mm}";
            File.WriteAllText(path, owner);
            return new ScanLock(path, owner);
        }

        private void Touch()
        {
            try { File.WriteAllText(path, owner); } catch { }
        }

        public void Dispose()
        {
            heartbeat.Dispose();
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Trim() == owner) File.Delete(path);
            }
            catch { }
        }
    }
}

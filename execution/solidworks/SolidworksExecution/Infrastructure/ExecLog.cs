using System;
using System.IO;

namespace SolidworksExecution.Infrastructure
{
    // Minimal file logger (P0.6-mini). Writes next to the exe as execution.log.
    // Must NEVER throw — logging failures are swallowed.
    public static class ExecLog
    {
        private static readonly object _lock = new object();

        public static readonly string FilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "execution.log");

        // In-process host (SolidPilot Bridge add-in): redirects every line into the add-in's own log, because BaseDirectory there is the SolidWorks install folder.
        public static Action<string> Sink;

        public static void Write(string message)
        {
            try
            {
                var sink = Sink;
                if (sink != null) { sink(message); return; }
                lock (_lock)
                {
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch
            {
                // logging must never break execution
            }
        }
    }
}

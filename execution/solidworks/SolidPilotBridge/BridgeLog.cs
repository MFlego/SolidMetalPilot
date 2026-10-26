using System;
using System.Collections.Generic;
using System.IO;

namespace SolidPilot.Bridge
{
    public static class BridgeLog
    {
        private static readonly object L = new object();
        private static readonly LinkedList<string> Ring = new LinkedList<string>();
        private static readonly string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidPilot", "bridge.log");
        public static void Info(string m) { Write("INFO", m); }
        public static void Error(string m, Exception e) { Write("ERROR", m + ": " + e); }
        private static void Write(string lvl, string m)
        {
            var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + lvl + " " + m;
            lock (L)
            {
                Ring.AddLast(line); while (Ring.Count > 200) Ring.RemoveFirst();
                try { Directory.CreateDirectory(Path.GetDirectoryName(LogFile)); File.AppendAllText(LogFile, line + Environment.NewLine); } catch { }
            }
        }
        public static string[] Recent(int n) { lock (L) { var a = new List<string>(Ring); int take = Math.Min(n, a.Count); return a.GetRange(a.Count - take, take).ToArray(); } }
    }
}

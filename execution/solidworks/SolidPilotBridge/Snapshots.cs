// Automatic safety net: a silent SaveAs-COPY of the active document before a tool call that can change or destroy work, at most once per interval per document, so a SolidWorks crash never
// costs more than that interval of work. The document keeps its name, path and modified state (Extension.SaveAs3 with swSaveAsOptions_Copy); copies go to %LOCALAPPDATA%\SolidPilot\snapshots.
// OFF by default until it has proven harmless: pipe command "snapshot_policy <seconds|off>" (or env SOLIDPILOT_SNAPSHOT_SEC at start-up) switches it on. Best effort: it never blocks or fails a tool call.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidPilot.Bridge
{
    public static class Snapshots
    {
        /// <summary>Minimum seconds between snapshots of the same document; 0 = off.</summary>
        public static int IntervalSeconds;
        public static int Keep = 8;                 // snapshots kept per document
        public static string LastResult = "never run";
        private static readonly Dictionary<string, DateTime> LastTaken = new Dictionary<string, DateTime>();
        private static readonly Regex ToolName = new Regex("\"tool\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        // tools that only read, export or switch documents never need a snapshot; everything else (incl. close_document, which can discard unsaved work) does
        private static readonly HashSet<string> NoSnapshot = new HashSet<string>
        {
            "analyze_model", "analyze_slddrw_test", "analyze_assembly", "get_selection", "verify_state", "export_document", "batch_export", "save_document",
            "open_document", "open_new_part", "open_new_assembly", "activate_document"
        };

        public static string Folder { get { return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SolidPilot", "snapshots"); } }

        public static void InitFromEnvironment()
        {
            int s; if (int.TryParse(System.Environment.GetEnvironmentVariable("SOLIDPILOT_SNAPSHOT_SEC"), out s) && s > 0) IntervalSeconds = s;
        }

        public static string ToolOf(string requestBody) { var m = ToolName.Match(requestBody ?? ""); return m.Success ? m.Groups[1].Value : null; }

        /// <summary>Called on the UI thread right before a tool call runs.</summary>
        public static void BeforeTool(SldWorks sw, string requestBody)
        {
            if (IntervalSeconds <= 0 || sw == null) return;
            var tool = ToolOf(requestBody); if (tool == null || NoSnapshot.Contains(tool)) return;
            try { Take(sw, false, "before " + tool); } catch (Exception e) { LastResult = "failed: " + e.Message; BridgeLog.Error("snapshot", e); }
        }

        /// <summary>Write a snapshot of the active document. Unless <paramref name="force"/>, only when it has unsaved changes and the interval has passed.</summary>
        public static string Take(SldWorks sw, bool force, string reason)
        {
            var doc = sw.ActiveDoc as ModelDoc2; if (doc == null) return LastResult = "no active document";
            if (!force && !doc.GetSaveFlag()) return LastResult = "skipped: " + doc.GetTitle() + " has no unsaved changes";
            string title = doc.GetTitle(); DateTime last;
            if (!force && LastTaken.TryGetValue(title, out last) && (DateTime.UtcNow - last).TotalSeconds < IntervalSeconds) return LastResult = "skipped: " + title + " was snapshotted " + (int)(DateTime.UtcNow - last).TotalSeconds + " s ago";
            string ext = doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY ? ".SLDASM" : doc.GetType() == (int)swDocumentTypes_e.swDocDRAWING ? ".SLDDRW" : ".SLDPRT";
            string stem = Path.GetFileNameWithoutExtension(title); foreach (var c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, stem + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ext);
            int err = 0, warn = 0; var t = System.Diagnostics.Stopwatch.StartNew();
            bool ok = doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (int)swSaveAsOptions_e.swSaveAsOptions_Copy, null, null, ref err, ref warn);
            LastTaken[title] = DateTime.UtcNow;
            if (ok) Prune(stem, ext);
            LastResult = (ok ? "saved " : "FAILED (err " + err + ") ") + path + " in " + t.ElapsedMilliseconds + " ms (" + reason + ")";
            BridgeLog.Info("snapshot " + LastResult); return LastResult;
        }

        private static void Prune(string stem, string ext)
        {
            try { foreach (var f in new DirectoryInfo(Folder).GetFiles(stem + "_*" + ext).OrderByDescending(x => x.LastWriteTimeUtc).Skip(Keep)) f.Delete(); } catch { }
            // overall cap too (new documents get a new stem each time: Part1, Part2 ...): newest 40 files, nothing older than 3 days
            try
            {
                var all = new DirectoryInfo(Folder).GetFiles().OrderByDescending(x => x.LastWriteTimeUtc).ToList();
                foreach (var f in all.Skip(40).Concat(all.Where(x => x.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-3))).Distinct()) { try { f.Delete(); } catch { } }
            }
            catch { }
        }

        public static string ListJson()
        {
            try
            {
                var files = Directory.Exists(Folder) ? new DirectoryInfo(Folder).GetFiles().OrderByDescending(x => x.LastWriteTimeUtc).Take(15).Select(x => Json.Str(x.FullName + " (" + x.Length + " bytes, " + x.LastWriteTime.ToString("HH:mm:ss") + ")")) : Enumerable.Empty<string>();
                return "{\"intervalSeconds\":" + IntervalSeconds + ",\"keep\":" + Keep + ",\"lastResult\":" + Json.Str(LastResult) + ",\"folder\":" + Json.Str(Folder) + ",\"recent\":[" + string.Join(",", files.ToArray()) + "]}";
            }
            catch (Exception e) { return "{\"error\":" + Json.Str(e.Message) + "}"; }
        }
    }

    /// <summary>Crash forensics: the request being executed is written to disk before the call and removed after it. If the file is still there at the next start-up, the previous SolidWorks session
    /// died DURING that call - the next /health and the bridge log say which one.</summary>
    public static class Inflight
    {
        public static string PreviousSessionDiedDuring { get; private set; }
        private static string FilePath { get { return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SolidPilot", "inflight.txt"); } }

        public static void Begin(string requestBody)
        {
            try
            {
                var p = FilePath; Directory.CreateDirectory(Path.GetDirectoryName(p));
                var b = requestBody ?? ""; if (b.Length > 800) b = b.Substring(0, 800) + "...";
                File.WriteAllText(p, System.Diagnostics.Process.GetCurrentProcess().Id + "|" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "|" + b.Replace("\r", " ").Replace("\n", " "));
            }
            catch { }
        }

        public static void End() { try { File.Delete(FilePath); } catch { } }

        /// <summary>At start-up: a leftover file whose process is gone means the previous session died mid-call.</summary>
        public static void CheckPrevious()
        {
            try
            {
                var p = FilePath; if (!File.Exists(p)) return;
                var parts = File.ReadAllText(p).Split(new[] { '|' }, 3); int pid; int.TryParse(parts[0], out pid);
                bool alive = false; try { alive = pid > 0 && !System.Diagnostics.Process.GetProcessById(pid).HasExited && pid != System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
                if (!alive) { PreviousSessionDiedDuring = (parts.Length > 1 ? parts[1] : "?") + " " + (parts.Length > 2 ? parts[2] : ""); BridgeLog.Info("PREVIOUS SOLIDWORKS SESSION DIED DURING A TOOL CALL: " + PreviousSessionDiedDuring); File.Delete(p); }
            }
            catch { }
        }
    }
}

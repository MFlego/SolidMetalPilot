using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;

namespace SolidworksExecution.Infrastructure
{
    /// <summary>
    /// Client for the in-process SolidPilot Bridge add-in (named pipe \\.\pipe\SolidPilotBridge). The bridge lives INSIDE SolidWorks, so it can see modal dialogs and
    /// the UI thread's state, which an out-of-process COM client cannot (a modal dialog just hangs the COM call). Every method is optional and failure-tolerant:
    /// when the add-in is not loaded the execution layer behaves exactly as before.
    /// </summary>
    public static class BridgeClient
    {
        public const string PipeName = "SolidPilotBridge";

        /// <summary>One request/response round trip. Returns the payload after "OK ", or null when the bridge is absent / slow / replied ERR.</summary>
        public static string Query(string command, int timeoutMs = 1500)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    c.Connect(timeoutMs);
                    var w = new StreamWriter(c, new UTF8Encoding(false)) { AutoFlush = true };
                    var r = new StreamReader(c, Encoding.UTF8);
                    w.WriteLine(command);
                    var read = Task.Run(() => r.ReadLine());
                    if (!read.Wait(timeoutMs)) return null;
                    var line = read.Result;
                    return line != null && line.StartsWith("OK ", StringComparison.Ordinal) ? line.Substring(3) : null;
                }
            }
            catch { return null; }
        }

        public static bool IsAvailable { get { return Query("ping", 300) != null; } }

        /// <summary>The bridge's one-shot status JSON (uiResponsive, openCommand, activeDocument, modalDialogs[]) or null when the bridge is not reachable.</summary>
        public static string State() { return Query("state", 3000); }

        /// <summary>The modal dialogs currently open in SolidWorks as JSON, or null when the bridge is not reachable. "[]" means none.</summary>
        public static string Dialogs() { return Query("dialogs", 1500); }

        /// <summary>True (with the dialog JSON) when at least one modal dialog is open.</summary>
        public static bool TryGetBlockingDialog(out string dialogsJson)
        {
            // Local Win32 check (~1 ms, works without the bridge). The bridge pipe query it replaced cost ~100 ms per tool call.
            dialogsJson = LocalDialogs.Json();
            return !string.IsNullOrEmpty(dialogsJson) && dialogsJson != "[]";
        }
    }

    /// <summary>A tool call was refused or aborted because SolidWorks is blocked (modal dialog) or not responding. Carries the dialog JSON when known.</summary>
    public class SolidWorksBlockedException : Exception
    {
        public string Code { get; private set; }
        public string DialogsJson { get; private set; }
        public SolidWorksBlockedException(string code, string message, string dialogsJson) : base(message) { Code = code; DialogsJson = dialogsJson; }
    }
}

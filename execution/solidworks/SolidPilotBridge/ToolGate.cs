// The safety gate every tool request passes before it touches SolidWorks - the in-process twin of the execution layer's StaExecutor, with the same codes and the same texts:
//   DIALOG_BLOCKING  a modal dialog is open (its text is in the message): the call could never finish
//   SW_BUSY          a previous call is still running (or timed out and is still stuck), or the UI thread does not answer
//   STA_TIMEOUT      the call exceeded the limit (env SWMCP_STA_TIMEOUT_SEC, default 120 s); the caller gets the answer, the call finishes (or not) on its own
// Exactly ONE tool call is ever handed to SolidWorks' UI thread at a time (a second one queues here, not inside SolidWorks' message pump, where it would run nested in the first).
using System;
using System.Threading;
using SolidWorks.Interop.sldworks;

namespace SolidPilot.Bridge
{
    public static class ToolGate
    {
        /// <summary>The live SolidWorks (set by the add-in); used for the automatic snapshot before a tool call.</summary>
        public static SldWorks App;

        private sealed class Call
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public ToolReply Reply; public Exception Error; public bool Finished;
        }

        private static readonly SemaphoreSlim One = new SemaphoreSlim(1, 1);
        private static readonly object StateLock = new object();
        private static long _busySinceTicks;       // 0 = idle, else UTC ticks when the running job started
        private static bool _abandoned;            // a timed-out job is still running on the UI thread

        public static int TimeoutSeconds
        {
            get { int s; return int.TryParse(System.Environment.GetEnvironmentVariable("SWMCP_STA_TIMEOUT_SEC"), out s) && s > 0 ? s : 120; }
        }

        /// <summary>A tool call is queued or running (so the tools generation must not be swapped and no extra work should be sent to the UI thread).</summary>
        public static bool IsBusy { get { return One.CurrentCount == 0; } }

        public static int BusySeconds
        {
            get { lock (StateLock) { return _busySinceTicks == 0 ? 0 : (int)(DateTime.UtcNow - new DateTime(_busySinceTicks, DateTimeKind.Utc)).TotalSeconds; } }
        }

        public static ToolReply Error(int status, string message) { return new ToolReply(status, "{\"error\":" + Json.Str(message) + "}"); }

        /// <summary>POST /api/tool/execute (and the pipe command "tool"). Never throws; a refusal is an ordinary FAILED tool response with a stable code.</summary>
        public static ToolReply Execute(IToolsHost host, string body)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // pre-flight, without touching SolidWorks: a modal dialog means the call could never finish. A dialog that flashes up and goes away within 1.5 s is not worth a refusal.
            var dialogs = Dialogs.List();
            for (int waited = 0; dialogs.Count > 0 && waited < 1500; waited += 250) { Thread.Sleep(250); dialogs = Dialogs.List(); }
            if (dialogs.Count > 0)
                return host.Failed(body, "DIALOG_BLOCKING", "SolidWorks has a modal dialog open: " + Dialogs.ToJson(dialogs) + " - answer it (bridge: dialog_respond <button>) or close it, then retry.");
            lock (StateLock) { if (_abandoned) return host.Failed(body, "SW_BUSY", "A previous SolidWorks call is still running (for " + BusySeconds + " s); SolidWorks is not responding or blocked."); }

            int timeout = TimeoutSeconds;
            if (!One.Wait(TimeSpan.FromSeconds(timeout)))
                return host.Failed(body, "SW_BUSY", "Another SolidWorks call has been running for " + BusySeconds + " s; this call waited " + timeout + " s for its turn.");
            // from here the semaphore is ours; the UI job releases it (also when we stop waiting for it)
            bool posted = false;
            try
            {
                try { UiThread.Run(() => 0, 5000); }
                catch (TimeoutException) { return host.Failed(body, "SW_BUSY", "SolidWorks' UI thread did not answer within 5 s (a long operation, or a dialog that is not visible yet); retry shortly."); }

                var call = new Call();
                long beforeJob = clock.ElapsedMilliseconds;
                UiThread.Post(() => RunJob(host, body, call)); posted = true;
                // how long did the call wait for SolidWorks' own UI work to drain before the tool could start? (the price of never cutting into its idle processing)
                if (beforeJob > 500) BridgeLog.Info("gate: " + beforeJob + " ms before the call could be handed to the UI thread (SolidWorks was busy)");
                if (!call.Done.Wait(TimeSpan.FromSeconds(timeout)))
                {
                    bool stillRunning;
                    lock (StateLock) { stillRunning = !call.Finished; if (stillRunning) _abandoned = true; }   // decided under the lock: the job may finish at this very moment
                    if (stillRunning)
                    {
                        var dlg = Dialogs.ToJson(Dialogs.List());
                        return host.Failed(body, "STA_TIMEOUT", "SolidWorks did not finish the call within " + timeout + " s" + (dlg != "[]" ? "; modal dialog(s) open: " + dlg : "; no dialog open") + ".");
                    }
                }
                if (call.Error != null) return Error(500, "STA execution failed: " + call.Error.Message);
                return call.Reply ?? Error(500, "tools host returned no reply");
            }
            finally { if (!posted) One.Release(); }
        }

        private static void RunJob(IToolsHost host, string body, Call call)
        {
            lock (StateLock) { _busySinceTicks = DateTime.UtcNow.Ticks; }
            try
            {
                Inflight.Begin(body);                    // crash forensics: still on disk at the next start-up = SolidWorks died during this call
                Snapshots.BeforeTool(App, body);         // best effort, off unless enabled; never fails the call
                call.Reply = host.Execute(body);
            }
            catch (Exception ex) { call.Error = ex; BridgeLog.Error("tool call", ex); }
            finally
            {
                Inflight.End();
                lock (StateLock) { _busySinceTicks = 0; _abandoned = false; call.Finished = true; }
                One.Release();
                call.Done.Set();
            }
        }

        /// <summary>GET /health and POST /ensure_ready: lifecycle calls that must answer even when SolidWorks is busy. While a tool call runs they are NOT sent to the UI thread (they would run nested inside it).</summary>
        public static ToolReply Lifecycle(IToolsHost host, bool ensureReady, string extraFieldsJson)
        {
            if (IsBusy)
                return new ToolReply(200, "{\"status\":\"UP\",\"inProcess\":true,\"comAttached\":true,\"toolCallRunning\":true,\"toolCallSeconds\":" + BusySeconds + "}");
            try { return UiThread.Run(() => ensureReady ? host.EnsureReady(extraFieldsJson) : host.Health(extraFieldsJson), 3000); }
            catch (TimeoutException)
            {
                return new ToolReply(200, "{\"status\":\"UP\",\"inProcess\":true,\"comAttached\":true,\"uiResponsive\":false,\"note\":\"the SolidWorks UI thread did not answer within 3 s (busy or blocked by a dialog)\"}");
            }
        }
    }
}

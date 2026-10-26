using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SolidworksExecution.Infrastructure
{
    /// <summary>
    /// Runs delegates on a single, long-lived, dedicated STA thread. (P0.1)
    ///
    /// SolidWorks COM objects must be called from an STA apartment. OWIN / Web API request
    /// threads are MTA, and while COM marshaling lets lightweight calls (e.g. FeatureExtrusion3)
    /// limp across the apartment boundary, heavier or more apartment-sensitive calls fail off-STA:
    /// InsertSheetMetalBaseFlange2 silently returned null and add_edge_feature (edge fillet)
    /// deadlocked (ContextSwitchDeadlock). Routing ALL COM-touching work through one STA thread —
    /// including the GetActiveObject attach — makes the RCW live on the STA, fixing both.
    ///
    /// A single worker thread also serializes COM access, which suits the single-writer,
    /// process-global state model the OperationGuard already assumes.
    ///
    /// SAFETY (SheetMetalPilot fork): the call is no longer unbounded. (1) Pre-flight: when the SolidPilot Bridge add-in is loaded, a modal dialog in SolidWorks is
    /// reported immediately as DIALOG_BLOCKING with its text instead of queueing a call that can never finish. (2) Timeout: a call that exceeds the limit
    /// (env SWMCP_STA_TIMEOUT_SEC, default 120 s) raises SolidWorksBlockedException (with the dialog list when known) instead of hanging the HTTP request forever.
    /// The worker thread stays inside the abandoned call until SolidWorks releases it; while it is busy, new calls fail fast with SW_BUSY.
    /// </summary>
    public sealed class StaExecutor
    {
        public static readonly StaExecutor Instance = new StaExecutor();

        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private long _busySinceTicks;        // 0 = idle, otherwise UTC ticks when the running job started
        private volatile bool _abandoned;    // a timed-out job is still running on the worker

        public static int TimeoutSeconds
        {
            get
            {
                int s; return int.TryParse(Environment.GetEnvironmentVariable("SWMCP_STA_TIMEOUT_SEC"), out s) && s > 0 ? s : 120;
            }
        }

        private StaExecutor()
        {
            var thread = new Thread(Worker)
            {
                IsBackground = true,
                Name = "SolidWorksStaThread"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void Worker()
        {
            // Each job sets its own completion event and captures its own exception, so a throwing
            // job never tears down the worker loop.
            foreach (var work in _queue.GetConsumingEnumerable())
                work();
        }

        /// <summary>Marshal <paramref name="func"/> onto the STA thread and block until it returns (bounded by the timeout).</summary>
        public T Run<T>(Func<T> func) { return Run(func, TimeoutSeconds); }

        public T Run<T>(Func<T> func, int timeoutSeconds)
        {
            // Pre-flight: refuse to queue work behind a modal dialog (bridge add-in required; silently skipped when it is not loaded).
            string dialogs;
            if (BridgeClient.TryGetBlockingDialog(out dialogs))
                throw new SolidWorksBlockedException("DIALOG_BLOCKING", "SolidWorks has a modal dialog open: " + dialogs + " - answer it (bridge: dialog_respond <button>) or close it, then retry.", dialogs);

            if (_abandoned)
            {
                var since = Interlocked.Read(ref _busySinceTicks);
                throw new SolidWorksBlockedException("SW_BUSY", "A previous SolidWorks call is still running" + (since > 0 ? " (for " + (int)(DateTime.UtcNow - new DateTime(since, DateTimeKind.Utc)).TotalSeconds + " s)" : "") + "; SolidWorks is not responding or blocked.", LocalDialogs.Json());
            }

            T result = default(T);
            Exception captured = null;
            using (var done = new ManualResetEventSlim(false))
            {
                _queue.Add(() =>
                {
                    Interlocked.Exchange(ref _busySinceTicks, DateTime.UtcNow.Ticks);
                    try { result = func(); }
                    catch (Exception ex) { captured = ex; }
                    finally { Interlocked.Exchange(ref _busySinceTicks, 0); _abandoned = false; done.Set(); }
                });
                if (!done.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    _abandoned = true;
                    var dlg = LocalDialogs.Json();
                    throw new SolidWorksBlockedException("STA_TIMEOUT", "SolidWorks did not finish the call within " + timeoutSeconds + " s" +
                        (!string.IsNullOrEmpty(dlg) && dlg != "[]" ? "; modal dialog(s) open: " + dlg : "; no dialog open") + ".", dlg);
                }
            }
            if (captured != null)
                throw new InvalidOperationException("STA execution failed: " + captured.Message, captured);
            return result;
        }
    }
}

// Runs delegates on SolidWorks' UI (STA) thread. Concept inspired by AngelSix SolidDna's ThreadHelpers (MIT, https://github.com/angelsix/solidworks-api):
// create a control on the UI thread once and Invoke through it. Unlike SolidDna it needs NO taskpane - a hidden control with a forced handle is enough.
using System;
using System.Threading;
using System.Windows.Forms;

namespace SolidPilot.Bridge
{
    public static class UiThread
    {
        private static Control _invoker;
        public static int UiThreadId { get; private set; }

        public static void Init()
        {
            _invoker = new Control();
            var h = _invoker.Handle;                  // force handle creation on THIS (UI) thread
            UiThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>Queue work onto the UI thread and return at once. The caller waits on its own handle, so it can give up while the job still completes later (ToolGate relies on this).
        /// The job must catch its own exceptions: an unhandled one would surface inside SolidWorks' message loop.</summary>
        public static void Post(Action job)
        {
            if (_invoker == null) throw new InvalidOperationException("UiThread not initialised");
            _invoker.BeginInvoke((MethodInvoker)delegate { job(); });
        }

        /// <summary>Run on the UI thread and return the result; throws TimeoutException instead of hanging the caller (e.g. when a modal dialog blocks the UI thread).</summary>
        public static T Run<T>(Func<T> f, int timeoutMs = 15000)
        {
            if (_invoker == null) throw new InvalidOperationException("UiThread not initialised");
            if (!_invoker.InvokeRequired) return f();
            T result = default(T); Exception err = null;
            var done = new ManualResetEventSlim(false);
            _invoker.BeginInvoke((MethodInvoker)delegate { try { result = f(); } catch (Exception e) { err = e; } finally { done.Set(); } });
            if (!done.Wait(timeoutMs)) throw new TimeoutException("SolidWorks UI thread did not respond within " + timeoutMs + " ms (a modal dialog is probably open)");
            if (err != null) throw err;
            return result;
        }
    }
}

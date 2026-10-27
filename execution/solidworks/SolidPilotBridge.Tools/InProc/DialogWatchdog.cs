// A tool that drives SolidWorks' user interface can be stopped dead by a dialog that SolidWorks raises INSIDE the click that the tool just posted: the dialog's modal loop then runs nested in the
// tool's own message pump, and the tool cannot continue until somebody answers it. (2026-10-27: picking the fourth rip sketch with the Overlap corner type raised "Overlaps/underlaps could not be
// applied to all rips" and the tool stood still for 100 s - the gate had long given up on it.) Only ANOTHER thread can answer in that situation, and the shell's Dialogs class works from any thread
// (a posted BM_CLICK needs neither focus nor the UI thread).
// The watchdog answers dialogs that have exactly ONE button (information boxes: there is nothing to decide) and records every dialog it sees. A dialog with a choice (Yes / No ...) is recorded but
// left alone - a person has to make that call - and the tool treats it as a failure.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SolidPilot.Bridge;

namespace SolidworksExecution.Services
{
    internal sealed class DialogWatchdog : IDisposable
    {
        private readonly Thread _thread; private volatile bool _stop;
        private readonly object _lock = new object(); private readonly List<string> _seen = new List<string>(); private readonly HashSet<long> _handled = new HashSet<long>();
        private string _undecided;

        public DialogWatchdog()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "SolidPilotDialogWatchdog" }; _thread.Start();
        }

        private void Loop()
        {
            while (!_stop)
            {
                try
                {
                    foreach (var d in Dialogs.List())
                    {
                        string text = "[" + d.Title + "] " + string.Join(" | ", d.Text.ToArray());
                        lock (_lock)
                        {
                            if (!_handled.Add(d.Handle)) continue;
                            _seen.Add(text);
                            if (d.Buttons.Count != 1) { _undecided = text + " (buttons: " + string.Join(", ", d.Buttons.ToArray()) + ")"; continue; }
                        }
                        Dialogs.Respond(d.Buttons[0]);
                    }
                }
                catch (Exception) { }
                Thread.Sleep(200);
            }
        }

        /// <summary>Every dialog seen so far (answered or not), oldest first.</summary>
        public List<string> Messages { get { lock (_lock) return _seen.ToList(); } }

        /// <summary>A dialog with a choice that nobody answered (null when there is none).</summary>
        public string Undecided { get { lock (_lock) return _undecided; } }

        public int Count { get { lock (_lock) return _seen.Count; } }

        public void Dispose() { _stop = true; }
    }
}

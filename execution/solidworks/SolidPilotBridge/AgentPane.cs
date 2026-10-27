// The "SolidPilot" task pane: the observer's window onto the agent - what it is doing now, the log of its recent tool calls, a STOP button (the next call is refused with USER_PAUSED until Resume) and a
// snapshot button. The user stays "on the loop" without being in it. Concept after the task-pane integration of AngelSix's SolidDna (MIT, https://github.com/angelsix/solidworks-api); no code is copied.
// The pane is a plain WinForms control shown through ITaskpaneView.DisplayWindowFromHandlex64, so it needs no COM registration of its own. It lives in the stable shell, therefore everything here is
// defensive: a failure only loses the pane (logged), never the add-in; timer ticks never throw into SolidWorks' message loop. Disable with SOLIDPILOT_PANE=0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace SolidPilot.Bridge
{
    internal sealed class AgentPane : UserControl
    {
        private readonly SldWorks _sw;
        private readonly Label _title, _state, _footer;
        private readonly Button _stop, _resume, _snapshot, _folder;
        private readonly ListView _list;
        private readonly Timer _timer;
        private long _shownTotal = -1; private string _shownState = "";

        public AgentPane(SldWorks sw)
        {
            _sw = sw; SuspendLayout();
            Font = new Font("Segoe UI", 9f); BackColor = SystemColors.Window; Dock = DockStyle.Fill;
            _title = new Label { Dock = DockStyle.Top, Height = 24, Text = "SolidPilot bridge " + PipeServer.Version, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Padding = new Padding(6, 4, 0, 0) };
            _state = new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 4, 6, 0), Text = "..." };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(4, 2, 0, 0), WrapContents = false };
            _stop = new Button { Text = "STOP agent", Width = 90, Height = 28, BackColor = Color.FromArgb(200, 40, 40), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            _resume = new Button { Text = "Resume", Width = 70, Height = 28, Enabled = false };
            _snapshot = new Button { Text = "Snapshot", Width = 74, Height = 28 };
            _folder = new Button { Text = "Folder", Width = 56, Height = 28 };
            buttons.Controls.AddRange(new Control[] { _stop, _resume, _snapshot, _folder });
            _list = new ListView { Dock = DockStyle.Fill, View = System.Windows.Forms.View.Details, FullRowSelect = true, GridLines = false, HeaderStyle = ColumnHeaderStyle.Nonclickable };
            _list.Columns.Add("Time", 62); _list.Columns.Add("Tool", 130); _list.Columns.Add("Result", 110); _list.Columns.Add("ms", 54, HorizontalAlignment.Right);
            _footer = new Label { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(6, 2, 6, 2), ForeColor = SystemColors.GrayText, Text = "STOP refuses the NEXT call; a call already running in SolidWorks cannot be interrupted." };
            Controls.Add(_list); Controls.Add(_footer); Controls.Add(buttons); Controls.Add(_state); Controls.Add(_title);
            _stop.Click += (s, e) => Safe(() => { ToolGate.PausedReason = "stopped from the SolidPilot task pane"; Refresh_(true); });
            _resume.Click += (s, e) => Safe(() => { ToolGate.PausedReason = null; Refresh_(true); });
            _snapshot.Click += (s, e) => Safe(() => { _footer.Text = Snapshots.Take(_sw, true, "task pane"); });
            _folder.Click += (s, e) => Safe(() => { Directory.CreateDirectory(Snapshots.Folder); Process.Start("explorer.exe", Snapshots.Folder); });
            _timer = new Timer { Interval = 1000 }; _timer.Tick += (s, e) => Safe(() => Refresh_(false)); _timer.Start();
            ResumeLayout(); Refresh_(true);
        }

        private static void Safe(Action a) { try { a(); } catch (Exception e) { BridgeLog.Error("task pane", e); } }

        private void Refresh_(bool force)
        {
            var paused = ToolGate.PausedReason; bool busy = ToolGate.IsBusy;
            string state = paused != null ? "PAUSED - " + paused : busy ? "RUNNING  " + (ToolGate.CurrentTool ?? "a tool") + "  (" + ToolGate.BusySeconds + " s)" : "Idle - waiting for the agent";
            string line2 = "tools generation " + ToolsLoader.Generation + "  |  calls " + Activity.Total + (Dialogs.List().Count > 0 ? "  |  A DIALOG IS OPEN" : "");
            if (force || state != _shownState || line2 != _stateLine2) { _state.Text = state + "\r\n" + line2; _stateLine2 = line2; _shownState = state; }
            _state.ForeColor = paused != null ? Color.FromArgb(200, 40, 40) : busy ? Color.FromArgb(180, 110, 0) : Color.FromArgb(30, 120, 60);
            _stop.Enabled = paused == null; _resume.Enabled = paused != null;
            if (force || Activity.Total != _shownTotal)
            {
                _shownTotal = Activity.Total; _list.BeginUpdate(); _list.Items.Clear();
                var rows = Activity.Recent(150);
                for (int i = rows.Count - 1; i >= 0; i--)                     // newest first
                {
                    var r = rows[i]; var item = new ListViewItem(r.Utc.ToLocalTime().ToString("HH:mm:ss")); item.SubItems.Add(r.Tool); item.SubItems.Add(r.Result); item.SubItems.Add(r.Ms.ToString());
                    item.ForeColor = r.Result.StartsWith("COMPLETED") ? Color.FromArgb(30, 120, 60) : r.Result.StartsWith("DUPLICATE") ? SystemColors.GrayText : Color.FromArgb(200, 40, 40);
                    _list.Items.Add(item);
                }
                _list.EndUpdate();
            }
        }
        private string _stateLine2 = "";

        public void Shutdown() { try { _timer.Stop(); _timer.Dispose(); } catch { } }
    }

    /// <summary>Creates and removes the task pane tab (UI thread only).</summary>
    internal static class AgentPaneHost
    {
        private static TaskpaneView _view; private static AgentPane _control;

        public static bool Disabled { get { return System.Environment.GetEnvironmentVariable("SOLIDPILOT_PANE") == "0"; } }

        public static void Create(SldWorks sw)
        {
            if (Disabled) { BridgeLog.Info("task pane disabled (SOLIDPILOT_PANE=0)"); return; }
            try
            {
                var dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SolidPilot", "pane");
                Directory.CreateDirectory(dir);
                _view = sw.CreateTaskpaneView3(WriteIcons(dir), "SolidPilot - what the agent is doing, and STOP");
                if (_view == null) { BridgeLog.Info("task pane: CreateTaskpaneView3 returned null"); return; }
                _control = new AgentPane(sw);
                bool ok = _view.DisplayWindowFromHandlex64(_control.Handle.ToInt64());
                BridgeLog.Info("task pane created (window shown: " + ok + ")");
            }
            catch (Exception e) { BridgeLog.Error("task pane create", e); }
        }

        public static void Destroy()
        {
            try { if (_control != null) _control.Shutdown(); } catch { }
            try { if (_view != null) _view.DeleteView(); } catch { }
            _view = null; _control = null;
        }

        // the tab icon: a rounded square with a "P", in the sizes SolidWorks asks for
        private static string[] WriteIcons(string dir)
        {
            var paths = new List<string>();
            foreach (int px in new[] { 20, 32, 40, 64, 96, 128 })
            {
                var path = Path.Combine(dir, "pane_" + px + ".png");
                if (!File.Exists(path))
                    using (var bmp = new Bitmap(px, px))
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAlias; g.Clear(Color.Transparent);
                        using (var b = new SolidBrush(Color.FromArgb(31, 111, 139))) g.FillEllipse(b, 1, 1, px - 3, px - 3);
                        using (var f = new Font("Segoe UI", px * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var w = new SolidBrush(Color.White)) { var s = g.MeasureString("P", f); g.DrawString("P", f, w, (px - s.Width) / 2f, (px - s.Height) / 2f); }
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                paths.Add(path);
            }
            return paths.ToArray();
        }
    }
}

// Command / document / SELECTION events seen from INSIDE SolidWorks: lets the framework know when a command (e.g. Convert to Sheet Metal) opens or closes, when the
// active document changes, and - go/no-go probe for the add-in route - every NEW selection with its type and MARK (the mark tells which PropertyManager selection box a pick
// went into). Nothing is polled from outside.
using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;

namespace SolidPilot.Bridge
{
    public class BridgeEvents
    {
        private readonly SldWorks _sw;
        private readonly object _l = new object();
        private readonly List<string> _events = new List<string>();
        private PartDoc _part;
        public int OpenCommand { get; private set; }
        private long _lastIdleTicks = DateTime.UtcNow.Ticks;
        /// <summary>Milliseconds since SolidWorks last reported idle (OnIdleNotify). Large while a long command / rebuild runs - a cheap "SolidWorks is busy" signal.</summary>
        public long IdleAgeMs { get { return (DateTime.UtcNow.Ticks - System.Threading.Interlocked.Read(ref _lastIdleTicks)) / TimeSpan.TicksPerMillisecond; } }

        public BridgeEvents(SldWorks sw) { _sw = sw; }

        public void Attach()
        {
            _sw.CommandOpenPreNotify += OnOpen; _sw.CommandCloseNotify += OnClose; _sw.ActiveDocChangeNotify += OnDoc;
            _sw.OnIdleNotify += OnIdle; _sw.FileOpenPreNotify += OnFileOpenPre; _sw.FileOpenPostNotify += OnFileOpenPost; _sw.FileNewNotify2 += OnFileNew;
            AttachDoc();
        }
        public void Detach()
        {
            _sw.CommandOpenPreNotify -= OnOpen; _sw.CommandCloseNotify -= OnClose; _sw.ActiveDocChangeNotify -= OnDoc;
            _sw.OnIdleNotify -= OnIdle; _sw.FileOpenPreNotify -= OnFileOpenPre; _sw.FileOpenPostNotify -= OnFileOpenPost; _sw.FileNewNotify2 -= OnFileNew;
            DetachDoc();
        }

        private void AttachDoc()
        {
            DetachDoc();
            try
            {
                _part = _sw.ActiveDoc as PartDoc;
                if (_part != null) { _part.NewSelectionNotify += OnNewSelection; _part.ClearSelectionsNotify += OnClearSelection; _part.UserSelectionPostNotify += OnUserSelPost; _part.UserSelectionPreNotify += OnUserSelPre; Add("selection-events attached to part"); }
            }
            catch (Exception e) { BridgeLog.Error("AttachDoc", e); _part = null; }
        }
        private void DetachDoc()
        {
            try { if (_part != null) { _part.NewSelectionNotify -= OnNewSelection; _part.ClearSelectionsNotify -= OnClearSelection; _part.UserSelectionPostNotify -= OnUserSelPost; _part.UserSelectionPreNotify -= OnUserSelPre; } } catch { }
            _part = null;
        }

        private int OnOpen(int command, int userCommand) { OpenCommand = command; Add("command-open " + command + " user=" + userCommand); return 0; }
        private int OnClose(int command, int reason) { OpenCommand = 0; Add("command-close " + command + " reason=" + reason); return 0; }
        private int OnIdle() { System.Threading.Interlocked.Exchange(ref _lastIdleTicks, DateTime.UtcNow.Ticks); return 0; }     // not logged: fires constantly
        private int OnFileOpenPre(string file) { Add("file-open-pre " + file); return 0; }
        private int OnFileOpenPost(string file) { Add("file-open-post " + file); return 0; }
        private int OnFileNew(object doc, int docType, string template) { Add("file-new type=" + docType); return 0; }
        private int OnDoc() { Add("active-doc-change"); AttachDoc(); return 0; }

        private int OnNewSelection()
        {
            try { Add("selection-new " + Describe(true)); } catch (Exception e) { BridgeLog.Error("OnNewSelection", e); }
            return 0;
        }
        private int OnClearSelection() { Add("selection-clear"); return 0; }
        // USER picks (mouse / posted messages): these also fire while a PropertyManager is open, where NewSelectionNotify can stay silent.
        private int OnUserSelPre(int selType) { Add("user-select-pre type=" + selType); return 0; }
        private int OnUserSelPost() { try { Add("user-select-post " + Describe(true)); } catch (Exception e) { BridgeLog.Error("OnUserSelPost", e); } return 0; }

        /// <summary>Selection summary: count and, per object, swSelectType, MARK and a typed detail (edge midpoint mm, face box centre mm, feature name).
        /// lastOnly=true returns just the newest object (event use).</summary>
        public string Describe(bool lastOnly)
        {
            var doc = _sw.ActiveDoc as ModelDoc2; if (doc == null) return "no document";
            var sm = (SelectionMgr)doc.SelectionManager; int n = sm.GetSelectedObjectCount2(-1);
            var sb = new StringBuilder("n=" + n);
            for (int i = lastOnly ? n : 1; i <= n; i++)
            {
                if (i < 1) continue;
                sb.Append(" [#" + i + " type=" + sm.GetSelectedObjectType3(i, -1) + " mark=" + sm.GetSelectedObjectMark(i) + Detail(sm, i) + "]");
            }
            return sb.ToString();
        }

        private static string Detail(SelectionMgr sm, int i)
        {
            try
            {
                object o = sm.GetSelectedObject6(i, -1);
                var e = o as Edge;
                if (e != null)
                {
                    var c = (Curve)e.GetCurve(); double s, en; bool cl, per; c.GetEndParams(out s, out en, out cl, out per);
                    var p = (double[])c.Evaluate2((s + en) / 2, 0);
                    return string.Format(" edgeMid=({0:F2},{1:F2},{2:F2})mm", p[0] * 1000, p[1] * 1000, p[2] * 1000);
                }
                var f = o as Face2;
                if (f != null) { var b = (double[])f.GetBox(); return string.Format(" faceBoxCentre=({0:F1},{1:F1},{2:F1})mm", (b[0] + b[3]) * 500, (b[1] + b[4]) * 500, (b[2] + b[5]) * 500); }
                var ft = o as Feature; if (ft != null) return " feature=" + ft.Name;
                var sg = o as SketchSegment; if (sg != null) return " sketchSegment";
                return o == null ? " object=null" : " object=" + o.GetType().Name;
            }
            catch (Exception ex) { return " detailError=" + ex.GetType().Name; }
        }

        private void Add(string s)
        {
            lock (_l) { _events.Add(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s); if (_events.Count > 1000) _events.RemoveAt(0); }
            BridgeLog.Info(s);
        }
        public string[] Since(int skip) { lock (_l) { int k = Math.Min(Math.Max(skip, 0), _events.Count); return _events.GetRange(k, _events.Count - k).ToArray(); } }
        public int Count { get { lock (_l) { return _events.Count; } } }
    }
}

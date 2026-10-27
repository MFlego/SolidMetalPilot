// Named-pipe server (\\.\pipe\SolidPilotBridge), current-user only, one thread per client connection (a stuck client can no longer block the others).
// Line protocol: one command per line, one reply line: "OK <payload>" or "ERR <message>". JSON payloads for state / dialogs.
//   ping | state | dialogs | dialog_respond <button caption> | activeDoc | events [skip] | log | msgbox <text>
//   tools_info | reload_tools | tool <request json>      (the in-process tool implementations; see ToolsLoader / ToolGate / HttpFacade)
// Every call that touches SolidWorks runs through UiThread.Run (SolidWorks' own thread) with a timeout instead of a hang; dialog commands are plain Win32 and
// therefore still answer while a modal dialog blocks the UI thread.
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using SolidWorks.Interop.sldworks;

namespace SolidPilot.Bridge
{
    public class PipeServer
    {
        public const string PipeName = "SolidPilotBridge";
        public const string Version = "0.6";
        private readonly SldWorks _sw; private readonly BridgeEvents _ev; private readonly IBridgeServices _services;
        private Thread _t; private volatile bool _stop;
        /// <summary>Set by the add-in once the REST façade exists (for the "tools_info" reply).</summary>
        public HttpFacade Http;

        public PipeServer(SldWorks sw, BridgeEvents ev, IBridgeServices services) { _sw = sw; _ev = ev; _services = services; }
        public void Start() { _stop = false; _t = new Thread(Accept) { IsBackground = true, Name = "SolidPilotBridgePipe" }; _t.Start(); }
        public void Stop() { _stop = true; try { using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut)) c.Connect(200); } catch { } }

        private void Accept()
        {
            var sec = new PipeSecurity();
            // CreateNewInstance is REQUIRED with a custom PipeSecurity once more than one instance exists, otherwise every instance after the first throws UnauthorizedAccess
            // (seen in bridge.log 2026-10-26: "pipe accept: Access to the path is denied").
            sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            while (!_stop)
            {
                try
                {
                    var s = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, sec);
                    s.WaitForConnection(); if (_stop) { s.Dispose(); return; }
                    new Thread(() => Serve(s)) { IsBackground = true, Name = "SolidPilotBridgeClient" }.Start();
                }
                catch (Exception e) { BridgeLog.Error("pipe accept", e); Thread.Sleep(500); }
            }
        }

        private void Serve(NamedPipeServerStream s)
        {
            try
            {
                using (s)
                {
                    var r = new StreamReader(s, Encoding.UTF8); var w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true };
                    string line; while (!_stop && (line = r.ReadLine()) != null) w.WriteLine(Handle(line.Trim()));
                }
            }
            catch (Exception e) { BridgeLog.Error("pipe client", e); }
        }

        private string Handle(string cmd)
        {
            try
            {
                var parts = cmd.Split(new[] { ' ' }, 2); var name = parts[0].ToLowerInvariant(); var arg = parts.Length > 1 ? parts[1] : "";
                switch (name)
                {
                    case "ping": return Ok("pong v" + Version + " uiThread=" + UiThread.UiThreadId + " openCommand=" + _ev.OpenCommand);
                    case "state": return Ok(State());
                    case "dialogs": return Ok(Dialogs.ToJson(Dialogs.List()));
                    case "selection": return Ok(UiThread.Run(() => _ev.Describe(false)));
                    case "run_cs": return Ok(ScriptRunner.Run(_sw, arg, 60000));
                    case "windows": return Ok(WindowProbe.Tree(arg));
                    case "post_click": return Ok(WindowProbe.PostClick(arg));
                    case "dialog_respond": return Ok(Dialogs.Respond(arg));
                    case "activedoc": return Ok(UiThread.Run(() => { var d = (ModelDoc2)_sw.ActiveDoc; return d == null ? "none" : d.GetTitle() + " | " + d.GetPathName(); }));
                    case "events": { int skip; int.TryParse(arg, out skip); return Ok(string.Join(" ;; ", _ev.Since(skip)) + " ## total=" + _ev.Count); }
                    case "log": return Ok(string.Join(" ;; ", BridgeLog.Recent(20)));
                    case "msgbox": return Ok(UiThread.Run(() => _sw.SendMsgToUser2(arg, 2, 2).ToString()));
                    // --- in-process tools (the same code the REST façade serves) ---
                    case "tools_info": return Ok("{\"tools\":" + ToolsLoader.InfoJson() + ",\"http\":" + (Http == null ? "null" : Http.InfoJson()) + ",\"toolBusy\":" + (ToolGate.IsBusy ? "true" : "false") + ",\"toolBusySeconds\":" + ToolGate.BusySeconds + "}");
                    case "reload_tools":
                        if (ToolGate.IsBusy) return "ERR a tool call is running for " + ToolGate.BusySeconds + " s; reload when it has finished";
                        return Ok(ToolsLoader.Load(_services));
                    // --- safety net: silent SaveAs-copy snapshots of the active document (see Snapshots.cs) ---
                    case "snapshots": return Ok(Snapshots.ListJson());
                    case "snapshot_now": return Ok(UiThread.Run(() => Snapshots.Take(_sw, true, "manual"), 60000));
                    case "snapshot_policy":    // seconds between snapshots of a document before a mutating tool call, or "off"
                    {
                        int s; if (arg.Trim().ToLowerInvariant() == "off") Snapshots.IntervalSeconds = 0; else if (int.TryParse(arg, out s) && s > 0) Snapshots.IntervalSeconds = s; else return "ERR usage: snapshot_policy <seconds|off>";
                        return Ok(Snapshots.ListJson());
                    }
                    case "tool":      // one tool request through the same safety gate as POST /api/tool/execute: "OK <httpStatus> <json>"
                    {
                        var host = ToolsLoader.Current; if (host == null) return "ERR tools not loaded: " + ToolsLoader.LastError;
                        var reply = ToolGate.Execute(host, arg); return Ok(reply.HttpStatus + " " + reply.Json);
                    }
                    default: return "ERR unknown command '" + name + "'";
                }
            }
            catch (Exception e) { return "ERR " + e.GetType().Name + ": " + e.Message; }
        }

        // One-shot status: is the UI thread alive, is a command open, is a dialog up, is a tool call running, what is the active document.
        // While a tool call runs the UI thread is not asked anything (the question would run nested inside the call), so the document is then unknown.
        public string State()
        {
            var dialogs = Dialogs.List();
            bool busy = ToolGate.IsBusy, responsive = true; string doc = null;
            if (!busy)
            {
                try { doc = UiThread.Run(() => { var d = (ModelDoc2)_sw.ActiveDoc; return d == null ? null : d.GetTitle(); }, 1500); }
                catch (TimeoutException) { responsive = false; }
                catch (Exception) { }
            }
            return "{\"bridge\":" + Json.Str(Version) + ",\"uiThread\":" + UiThread.UiThreadId + ",\"uiResponsive\":" + (responsive ? "true" : "false")
                 + ",\"toolBusy\":" + (busy ? "true" : "false") + ",\"toolBusySeconds\":" + ToolGate.BusySeconds
                 + ",\"openCommand\":" + _ev.OpenCommand + ",\"idleAgeMs\":" + _ev.IdleAgeMs + ",\"activeDocument\":" + Json.Str(doc) + ",\"modalDialogs\":" + Dialogs.ToJson(dialogs) + "}";
        }
        private static string Ok(string s) { return "OK " + s.Replace("\r", " ").Replace("\n", " "); }
    }
}

// SolidPilot Bridge add-in entry point (ISwAddin).
// Design inspired by AngelSix's SolidDna (MIT, https://github.com/angelsix/solidworks-api): the add-in lifecycle (ConnectToSW / DisconnectFromSW) and the idea of
// marshalling work onto SolidWorks' UI thread with a control created on that thread. No SolidDna code is copied.
//
// The add-in is a small, stable SHELL: it owns the UI thread access, the safety gate (ToolGate), the REST façade (HttpFacade), the pipe (PipeServer), the events and the dialog handling.
// The tool implementations live in SolidPilotBridge.Tools.dll, which the shell loads from bytes (ToolsLoader) and can swap while SolidWorks keeps running.
using System;
using System.IO;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;

namespace SolidPilot.Bridge
{
    [ComVisible(true)]
    [Guid(BridgeAddin.AddinGuid)]
    [ProgId("SolidPilot.Bridge")]
    // Default class interface (AutoDispatch) on purpose: SetAddinCallbackInfo2 marshals `this` as IDispatch. With ClassInterfaceType.None the CCW has no IDispatch and the call throws
    // InvalidCastException (seen in bridge.log 2026-10-26).
    public class BridgeAddin : ISwAddin
    {
        public const string AddinGuid = "6F0B8E2A-3C41-4D6B-9A57-1E2D5C7A9B10";
        private SldWorks _sw;
        private BridgeEvents _events;
        private PipeServer _pipe;
        private HttpFacade _http;

        public bool ConnectToSW(object thisSw, int cookie)
        {
            try
            {
                _sw = (SldWorks)thisSw;
                _sw.SetAddinCallbackInfo2(0, this, cookie);
                UiThread.Init();                      // must run ON SolidWorks' UI thread: ConnectToSW does
                BridgeLog.Info("ConnectToSW on thread " + System.Threading.Thread.CurrentThread.ManagedThreadId + ", bridge " + PipeServer.Version);
                var services = new BridgeServices(_sw);
                Inflight.CheckPrevious();             // did the previous SolidWorks session die in the middle of a tool call?
                Snapshots.InitFromEnvironment(); ToolGate.App = _sw;
                _events = new BridgeEvents(_sw); _events.Attach();
                _pipe = new PipeServer(_sw, _events, services); _pipe.Start();
                StartTools(services);
                try { AgentPaneHost.Create(_sw); }    // the observer's task pane (STOP, activity log): its own try/catch, so that even a type-load failure can only cost the pane, never the add-in
                catch (Exception pe) { BridgeLog.Error("task pane", pe); }
                return true;
            }
            catch (Exception e) { BridgeLog.Error("ConnectToSW failed", e); return false; }
        }

        // The tools are optional at start-up: a missing or broken tools build must never stop the add-in (the pipe, events and dialog handling keep working).
        private void StartTools(IBridgeServices services)
        {
            try
            {
                var dir = System.Environment.GetEnvironmentVariable("SOLIDPILOT_TOOLS");     // (qualified: the SolidWorks interop also defines an Environment type)
                if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Path.GetDirectoryName(typeof(BridgeAddin).Assembly.Location) ?? "", "tools");
                ToolsLoader.Init(dir);
                ToolsLoader.Load(services);          // logs its own result
                if (HttpFacade.Disabled) { BridgeLog.Info("http façade disabled (SOLIDPILOT_HTTP=0)"); return; }
                _http = new HttpFacade(_pipe.State); _pipe.Http = _http; _http.Start();
            }
            catch (Exception e) { BridgeLog.Error("StartTools", e); }
        }

        public bool DisconnectFromSW()
        {
            try { AgentPaneHost.Destroy(); } catch (Exception pe) { BridgeLog.Error("task pane destroy", pe); }
            try
            {
                if (_http != null) _http.Stop();
                var tools = ToolsLoader.Current; if (tools != null) { try { tools.Shutdown(); } catch (Exception e) { BridgeLog.Error("tools Shutdown", e); } }
                if (_pipe != null) _pipe.Stop();
                if (_events != null) _events.Detach();
            }
            catch (Exception e) { BridgeLog.Error("DisconnectFromSW", e); }
            _http = null; _pipe = null; _events = null; _sw = null; BridgeLog.Info("DisconnectFromSW");
            return true;
        }
    }

    /// <summary>What the shell hands every tools generation.</summary>
    internal sealed class BridgeServices : IBridgeServices
    {
        private readonly SldWorks _sw;
        public BridgeServices(SldWorks sw) { _sw = sw; }
        public SldWorks SolidWorks { get { return _sw; } }
        public void Log(string message) { BridgeLog.Info(message); }
        public string DialogsJson() { return Dialogs.ToJson(Dialogs.List()); }
    }
}

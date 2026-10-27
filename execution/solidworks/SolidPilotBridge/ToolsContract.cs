// Contract between the add-in SHELL (stable: loaded once with SolidWorks, rarely rebuilt) and a TOOLS GENERATION (SolidPilotBridge.Tools.dll: the tool implementations, rebuilt and reloaded
// without restarting SolidWorks). Everything that crosses the boundary is a plain string or a simple type, so a new generation can sit next to the old one in the same AppDomain.
// SolidWorks API objects never cross it except the one ISldWorks the shell hands over at Initialize.
using System;
using SolidWorks.Interop.sldworks;

namespace SolidPilot.Bridge
{
    /// <summary>One HTTP-shaped reply: a status code and a JSON body.</summary>
    public sealed class ToolReply
    {
        public int HttpStatus;
        public string Json;
        public ToolReply() { }
        public ToolReply(int httpStatus, string json) { HttpStatus = httpStatus; Json = json; }
    }

    /// <summary>What the shell offers a tools generation.</summary>
    public interface IBridgeServices
    {
        /// <summary>The live SolidWorks application (the add-in runs inside it, so there is nothing to attach to).</summary>
        SldWorks SolidWorks { get; }
        void Log(string message);
        /// <summary>Modal dialogs open in SolidWorks right now, as a JSON array.</summary>
        string DialogsJson();
    }

    /// <summary>What a tools generation offers the shell. Execute / Health / EnsureReady are called ON SolidWorks' UI thread; State / Failed never touch SolidWorks.</summary>
    public interface IToolsHost
    {
        string Version { get; }
        void Initialize(IBridgeServices services);
        /// <summary>POST /api/tool/execute: the request body in, the HTTP status + JSON body out.</summary>
        ToolReply Execute(string requestJson);
        /// <summary>A FAILED tool response (HTTP 200) built without touching SolidWorks, used by the shell's safety gate (DIALOG_BLOCKING, SW_BUSY, STA_TIMEOUT).</summary>
        ToolReply Failed(string requestJson, string code, string message);
        /// <summary>GET /api/tool/state.</summary>
        ToolReply State();
        /// <summary>GET /health; <paramref name="extraFieldsJson"/> is a JSON object whose fields the shell wants merged into the reply (bridge state, generation ...).</summary>
        ToolReply Health(string extraFieldsJson);
        /// <summary>POST /ensure_ready.</summary>
        ToolReply EnsureReady(string extraFieldsJson);
        void Shutdown();
    }
}

// The in-process request handling for the shared tool code: the twin of the out-of-process ToolController, minus OWIN and the STA hop (the shell's ToolGate has already put us on
// SolidWorks' UI thread). Request and response JSON are byte-compatible with the execution layer: camelCase names, nulls omitted (same settings as its Startup.cs), same HTTP statuses,
// same error texts - so the Python adapter and the compiler cannot tell which host answered.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;
using SolidworksExecution.Services;

namespace SolidPilot.Bridge.Tools
{
    public sealed class ToolsHost : IToolsHost
    {
        private static readonly JsonSerializerSettings Wire = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };

        private IBridgeServices _services;
        private readonly DateTime _built = DateTime.UtcNow;

        public string Version { get { return "0.1 (" + typeof(ToolsHost).Assembly.GetName().Version + ", " + _built.ToString("HH:mm:ss") + "Z)"; } }

        public void Initialize(IBridgeServices services)
        {
            _services = services;
            ExecLog.Sink = services.Log;     // the shared code logs through ExecLog; in SolidWorks that goes to the bridge log
            ExecLog.Write("tools host initialised: " + Version);
        }

        public void Shutdown() { ExecLog.Write("tools host shutting down: " + Version); ExecLog.Sink = null; }

        private static ToolReply Reply(int status, object body) { return new ToolReply(status, JsonConvert.SerializeObject(body, Wire)); }

        public ToolReply State() { return Reply(200, new { stateVersion = OperationGuard.Instance.GetCurrentStateVersion() }); }

        public ToolReply Failed(string requestJson, string code, string message)
        {
            string op = null;
            try { var t = JObject.Parse(requestJson).GetValue("operationId", StringComparison.OrdinalIgnoreCase); op = t == null ? null : t.ToString(); } catch { }
            ExecLog.Write("<- FAILED " + code + ": " + message);
            return Reply(200, new ExecutionResponse
            {
                OperationId = op,
                Status = "FAILED",
                LastKnownStateVersion = OperationGuard.Instance.GetCurrentStateVersion(),
                Error = new ExecutionError { Code = code, Message = message }
            });
        }

        public ToolReply Execute(string requestJson)
        {
            ToolRequest request;
            try { request = JsonConvert.DeserializeObject<ToolRequest>(requestJson, Wire); }
            catch (Exception ex) { return Reply(400, new { error = "malformed JSON body: " + ex.Message }); }
            if (request == null || string.IsNullOrEmpty(request.OperationId) || string.IsNullOrEmpty(request.Tool))
                return Reply(400, new { error = "operation_id and tool are required." });

            ExecLog.Write("-> " + request.Tool + " op=" + request.OperationId + " sv=" + request.StateVersion);
            ExecutionResponse response;
            try
            {
                // a fresh service per request, exactly like the out-of-process controller (no instance state may leak between calls); it is handed the live ISldWorks
                response = ToolDispatcher.Dispatch(new SolidWorksService(OperationGuard.Instance, _services.SolidWorks), request);
            }
            catch (Exception ex)
            {
                ExecLog.Write("<- " + request.Tool + " UNHANDLED " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
                return Reply(500, new { error = "STA execution failed: " + ex.Message });
            }
            if (response == null) { ExecLog.Write("<- " + request.Tool + " UNKNOWN_TOOL"); return Reply(400, new { error = "Unknown tool: " + request.Tool }); }
            if (response.Status == "FAILED") ExecLog.Write("<- " + request.Tool + " FAILED " + (response.Error == null ? "" : response.Error.Code + ": " + response.Error.Message));
            else ExecLog.Write("<- " + request.Tool + " " + response.Status + " sv=" + response.StateVersion);
            return Reply(200, response);
        }

        // Same fields as the out-of-process /health, plus whatever the shell wants merged in (bridge state, generation, http info ...).
        public ToolReply Health(string extraFieldsJson)
        {
            var info = Lifecycle(false, extraFieldsJson);
            ExecLog.Write("<- health UP comAttached=" + info["comAttached"]);
            return Reply(200, info);
        }

        public ToolReply EnsureReady(string extraFieldsJson)
        {
            var info = Lifecycle(true, extraFieldsJson);
            ExecLog.Write("<- ensure_ready UP comAttached=" + info["comAttached"] + " swLaunched=False");
            return Reply(200, info);
        }

        private JObject Lifecycle(bool ensure, string extraFieldsJson)
        {
            var info = new JObject
            {
                ["status"] = "UP",
                ["stateVersion"] = OperationGuard.Instance.GetCurrentStateVersion(),
                ["serverTimeUtc"] = DateTime.UtcNow.ToString("o"),
                ["inProcess"] = true,
                ["toolsVersion"] = Version
            };
            // We ARE inside SolidWorks: attached by construction, never launched, and exactly one process (the duplicate-instance problem of the out-of-process host cannot exist here).
            info["comAttached"] = true;
            if (ensure) info["swLaunched"] = false;
            try { var d = _services.SolidWorks.IActiveDoc2 as IModelDoc2; info["activeDocument"] = d != null ? d.GetTitle() : null; } catch { }
            info["solidworksProcessCount"] = 1;
            info["solidworksPids"] = new JArray(Process.GetCurrentProcess().Id);
            info["solidworksWindowless"] = 0;
            if (!string.IsNullOrEmpty(extraFieldsJson))
            {
                try { foreach (var p in JObject.Parse(extraFieldsJson).Properties()) info[p.Name] = p.Value; } catch { }
            }
            return info;
        }
    }
}

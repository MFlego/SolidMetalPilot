using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;
using SolidworksExecution.Services;

namespace SolidworksExecution.Controllers
{
    [RoutePrefix("api/tool")]
    public class ToolController : ApiController
    {
        private readonly SolidWorksService _service;
        private readonly IOperationGuard _guard;

        public ToolController() : this(OperationGuard.Instance) { }

        public ToolController(IOperationGuard guard)
        {
            _guard = guard;
            _service = new SolidWorksService(guard);
        }

        // Read-only resync endpoint: returns the current authoritative state_version
        // WITHOUT a state_version check or increment. The adapter calls this to recover
        // from a desync (e.g. the execution server was restarted after a rebuild).
        [HttpGet]
        [Route("state")]
        public HttpResponseMessage GetState()
        {
            return Request.CreateResponse(HttpStatusCode.OK, new
            {
                stateVersion = _guard.GetCurrentStateVersion()
            });
        }

        // Health endpoint (P0.6). `~/` escapes the api/tool prefix → GET /health. Reports the server is
        // up, the current state_version, and whether SolidWorks COM is currently attachable (probed on the
        // STA thread — COM_ATTACH is lazy/per-request, so this is a fresh attach attempt) plus the active
        // document. Never 500s: a COM probe failure is reported as comAttached=false, not an error status.
        [HttpGet]
        [Route("~/health")]
        public HttpResponseMessage Health()
        {
            var info = new Dictionary<string, object>
            {
                ["status"] = "UP",
                ["stateVersion"] = _guard.GetCurrentStateVersion(),
                ["serverTimeUtc"] = DateTime.UtcNow.ToString("o"),
            };
            try
            {
                var com = StaExecutor.Instance.Run(() => _service.GetHealthInfo());
                foreach (var kv in com) info[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                info["comAttached"] = false;
                info["comError"] = ex.Message;
            }
            // SolidPilot Bridge add-in (in-process): UI-thread responsiveness, open command and modal dialogs. Absent bridge => bridgeLoaded=false, nothing else changes.
            var bridgeState = BridgeClient.State();
            info["bridgeLoaded"] = bridgeState != null;
            if (bridgeState != null) info["bridgeState"] = Newtonsoft.Json.Linq.JObject.Parse(bridgeState);
            info["staTimeoutSeconds"] = StaExecutor.TimeoutSeconds;
            object attached;
            info.TryGetValue("comAttached", out attached);
            ExecLog.Write($"<- health UP comAttached={attached} bridge={info["bridgeLoaded"]}");
            return Request.CreateResponse(HttpStatusCode.OK, info);
        }

        // Lifecycle bootstrap (ensure_ready). `~/` escapes the api/tool prefix → POST /ensure_ready.
        // POST because it is side-effecting: it launches SolidWorks via COM if it isn't running.
        // Distinct from /health (a read-only probe) — this one makes the environment ready. Does NOT
        // open/create a document. Runs on the STA thread (touches COM) and never 500s (a launch
        // failure is reported as comAttached=false). Not part of the state_version/idempotency
        // envelope — it's a lifecycle call, not a CAD operation.
        [HttpPost]
        [Route("~/ensure_ready")]
        public HttpResponseMessage EnsureReady()
        {
            var info = new Dictionary<string, object>
            {
                ["status"] = "UP",
                ["stateVersion"] = _guard.GetCurrentStateVersion(),
                ["serverTimeUtc"] = DateTime.UtcNow.ToString("o"),
            };
            try
            {
                var com = StaExecutor.Instance.Run(() => _service.EnsureReady());
                foreach (var kv in com) info[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                info["comAttached"] = false;
                info["ensureError"] = ex.Message;
            }
            object attached, launched;
            info.TryGetValue("comAttached", out attached);
            info.TryGetValue("swLaunched", out launched);
            ExecLog.Write($"<- ensure_ready UP comAttached={attached} swLaunched={launched}");
            return Request.CreateResponse(HttpStatusCode.OK, info);
        }

        [HttpPost]
        [Route("execute")]
        public HttpResponseMessage Execute([FromBody] ToolRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.OperationId) || string.IsNullOrEmpty(request.Tool))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "operation_id and tool are required." });

            ExecLog.Write($"-> {request.Tool} op={request.OperationId} sv={request.StateVersion}");

            ExecutionResponse response;

            try
            {
                // All COM-touching work runs on the single dedicated STA thread (P0.1) — OWIN request
                // threads are MTA, which made sheet-metal calls return null and add_edge_feature deadlock.
                response = StaExecutor.Instance.Run(() => Dispatch(request));
            }
            catch (SolidWorksBlockedException ex)
            {
                // SolidWorks is blocked by a modal dialog / not responding (SolidPilot Bridge pre-flight or STA timeout). Reported as an ordinary FAILED tool result with a
                // stable code so the agent can read the dialog text and decide, instead of an HTTP 500 or a hung request.
                ExecLog.Write($"<- {request.Tool} FAILED {ex.Code}: {ex.Message}");
                return Request.CreateResponse(HttpStatusCode.OK, new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "FAILED",
                    LastKnownStateVersion = _guard.GetCurrentStateVersion(),
                    Error = new ExecutionError { Code = ex.Code, Message = ex.Message }
                });
            }
            catch (System.Exception ex)
            {
                ExecLog.Write($"<- {request.Tool} UNHANDLED {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }

            if (response == null)
            {
                ExecLog.Write($"<- {request.Tool} UNKNOWN_TOOL");
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = $"Unknown tool: {request.Tool}" });
            }

            if (response.Status == "FAILED")
                ExecLog.Write($"<- {request.Tool} FAILED {response.Error?.Code}: {response.Error?.Message}");
            else
                ExecLog.Write($"<- {request.Tool} {response.Status} sv={response.StateVersion}");

            return Request.CreateResponse(HttpStatusCode.OK, response);
        }

        // Routes a tool request to its service method (the table is shared with the in-process host: Services/ToolDispatcher.cs). Runs on the STA thread (see Execute).
        // Returns null for an unknown tool so the caller can emit the BadRequest response.
        private ExecutionResponse Dispatch(ToolRequest request)
        {
            return ToolDispatcher.Dispatch(_service, request);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;
using SolidworksExecution.Services;

namespace SolidworksExecution.Controllers
{
    [RoutePrefix("api/tool")]
    public class ToolController : ApiController
    {
        private static readonly IOperationGuard _sharedGuard = OperationGuard.Instance;
        private static readonly SolidWorksService _sharedService;
        private static readonly ToolRegistry _sharedRegistry;

        static ToolController()
        {
            _sharedService = new SolidWorksService(_sharedGuard);
            _sharedRegistry = new ToolRegistry();
            _sharedRegistry.RegisterDrawing(_sharedService);
            _sharedRegistry.RegisterDocument(_sharedService);
            _sharedRegistry.RegisterPart(_sharedService);
            _sharedRegistry.RegisterSketch(_sharedService);
            _sharedRegistry.RegisterAssembly(_sharedService);
            _sharedRegistry.RegisterSheetMetal(_sharedService);
            _sharedRegistry.RegisterAnalysis(_sharedService);
        }

        private readonly SolidWorksService _service;
        private readonly IOperationGuard _guard;
        private readonly ToolRegistry _registry;

        public ToolController() : this(_sharedGuard) { }

        public ToolController(IOperationGuard guard)
        {
            _guard = guard ?? _sharedGuard;
            _service = _sharedService;
            _registry = _sharedRegistry;
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
        public async Task<HttpResponseMessage> Health()
        {
            var info = new Dictionary<string, object>
            {
                ["status"] = "UP",
                ["stateVersion"] = _guard.GetCurrentStateVersion(),
                ["serverTimeUtc"] = DateTime.UtcNow.ToString("o"),
            };
            try
            {
                var com = await StaExecutor.Instance.RunAsync(() => _service.GetHealthInfo());
                foreach (var kv in com) info[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                info["comAttached"] = false;
                info["comError"] = ex.Message;
            }
            object attached;
            info.TryGetValue("comAttached", out attached);
            ExecLog.Write($"<- health UP comAttached={attached}");
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
        public async Task<HttpResponseMessage> EnsureReady()
        {
            var info = new Dictionary<string, object>
            {
                ["status"] = "UP",
                ["stateVersion"] = _guard.GetCurrentStateVersion(),
                ["serverTimeUtc"] = DateTime.UtcNow.ToString("o"),
            };
            try
            {
                var com = await StaExecutor.Instance.RunAsync(() => _service.EnsureReady());
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
        public async Task<HttpResponseMessage> Execute([FromBody] ToolRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.OperationId) || string.IsNullOrEmpty(request.Tool))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "operation_id and tool are required." });

            ExecLog.Write($"-> {request.Tool} op={request.OperationId} sv={request.StateVersion}");

            ExecutionResponse response;

            try
            {
                // All COM-touching work runs on the single dedicated STA thread (P0.1) non-blockingly via RunAsync.
                response = await StaExecutor.Instance.RunAsync(() => _registry.Dispatch(request));
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
    }
}

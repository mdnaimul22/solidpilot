using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;
using SolidworksExecution.Services.Interfaces;

namespace SolidworksExecution.Services
{
    public partial class SolidWorksService :
        IDrawingService,
        IDocumentService,
        IPartService,
        ISketchService,
        IAssemblyService,
        ISheetMetalService,
        IAnalysisService
    {
        private readonly IOperationGuard _guard;
        private ISldWorks _solidWorks;
        private static readonly object _comLock = new object();
        private static string _cachedPartTemplate;
        private static string _cachedDrawingTemplate;

        public bool IsConnected { get; private set; }

        public SolidWorksService(IOperationGuard guard)
        {
            _guard = guard;
        }

        protected ExecutionResponse ExecuteGuarded(
            ToolRequest request,
            Func<IModelDoc2, ExecutionResponse> action,
            bool requiresDrawing = false,
            bool requiresPartOrAssembly = false,
            bool requiresActiveDoc = true,
            bool incrementsState = true)
        {
            var sw = Stopwatch.StartNew();
            ExecLog.Write($"[{request.Tool}] START op={request.OperationId} incomingState={request.StateVersion}");

            lock (_comLock)
            {
                if (_guard.IsDuplicate(request.OperationId))
                {
                    ExecLog.Write($"[{request.Tool}] DUPLICATE op={request.OperationId}");
                    return _guard.GetDuplicate(request.OperationId);
                }

                if (!_guard.IsStateVersionValid(request.StateVersion))
                {
                    int current = _guard.GetCurrentStateVersion();
                    ExecLog.Write($"[{request.Tool}] INVALID_STATE_VERSION expected={current} got={request.StateVersion}");
                    return BuildFailed(request.OperationId, current,
                        "INVALID_STATE_VERSION", $"Incoming state_version {request.StateVersion} does not match current state {current}.");
                }

                if (!EnsureConnected())
                {
                    ExecLog.Write($"[{request.Tool}] COM_ATTACH_FAILED op={request.OperationId}");
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");
                }

                try
                {
                    var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                    if (modelDoc == null && (requiresDrawing || requiresPartOrAssembly || requiresActiveDoc))
                    {
                        ExecLog.Write($"[{request.Tool}] NO_ACTIVE_DOCUMENT op={request.OperationId}");
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");
                    }

                    if (requiresPartOrAssembly)
                    {
                        if (modelDoc == null || !(modelDoc is IPartDoc || modelDoc is IAssemblyDoc))
                        {
                            var pd = FindOpenPartOrAssembly();
                            if (pd != null)
                            {
                                int actErr = 0;
                                try { _solidWorks.ActivateDoc3(pd.GetTitle(), false, 0, ref actErr); } catch { }
                                modelDoc = pd;
                            }
                        }
                        if (modelDoc == null || !(modelDoc is IPartDoc || modelDoc is IAssemblyDoc))
                        {
                            ExecLog.Write($"[{request.Tool}] WRONG_DOCUMENT_TYPE (not part or assembly) op={request.OperationId}");
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "WRONG_DOCUMENT_TYPE", $"{request.Tool} requires an active part or assembly document.");
                        }
                    }

                    if (requiresDrawing)
                    {
                        var drawingDoc = modelDoc as IDrawingDoc;
                        if (drawingDoc == null)
                        {
                            var dd = FindOpenDocument<IDrawingDoc>();
                            if (dd != null)
                            {
                                int actErr = 0;
                                try { _solidWorks.ActivateDoc3(((IModelDoc2)dd).GetTitle(), false, 0, ref actErr); } catch { }
                                modelDoc = (IModelDoc2)dd;
                                drawingDoc = dd;
                            }
                        }
                        if (drawingDoc == null)
                        {
                            ExecLog.Write($"[{request.Tool}] WRONG_DOCUMENT_TYPE (not a drawing) op={request.OperationId}");
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "WRONG_DOCUMENT_TYPE", $"{request.Tool} requires an active drawing document.");
                        }
                    }

                    var response = action(modelDoc);
                    if (response != null && response.Status == "COMPLETED")
                    {
                        if (incrementsState)
                        {
                            _guard.RegisterCompleted(request.OperationId, response);
                        }
                        ExecLog.Write($"[{request.Tool}] COMPLETED op={request.OperationId} elapsed={sw.ElapsedMilliseconds}ms nextState={response.StateVersion}");
                    }
                    else
                    {
                        ExecLog.Write($"[{request.Tool}] FAILED op={request.OperationId} elapsed={sw.ElapsedMilliseconds}ms code={response?.Error?.Code} msg={response?.Error?.Message}");
                    }
                    return response;
                }
                catch (COMException ex)
                {
                    ExecLog.Write($"[{request.Tool}] COM_ERROR op={request.OperationId} HRESULT=0x{ex.ErrorCode:X8} msg={ex.Message}\n{ex.StackTrace}");
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COM_ERROR", ex.Message);
                }
                catch (Exception ex)
                {
                    ExecLog.Write($"[{request.Tool}] UNEXPECTED_ERROR op={request.OperationId} msg={ex.Message}\n{ex.StackTrace}");
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.Message);
                }
            }
        }

        protected bool ValidateStandardGuards(ToolRequest request, out ExecutionResponse failedResponse)
        {
            failedResponse = null;
            if (_guard.IsDuplicate(request.OperationId))
            {
                failedResponse = _guard.GetDuplicate(request.OperationId);
                return false;
            }

            if (!_guard.IsStateVersionValid(request.StateVersion))
            {
                int current = _guard.GetCurrentStateVersion();
                failedResponse = BuildFailed(request.OperationId, current,
                    "INVALID_STATE_VERSION", $"Incoming state_version {request.StateVersion} does not match current state {current}.");
                return false;
            }

            if (!EnsureConnected())
            {
                failedResponse = BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");
                return false;
            }

            return true;
        }

        // SolidWorks COM must be called from an STA thread.
        // Ensure the calling thread is STA (e.g. [STAThread] on Main, or a dedicated STA thread).
        public bool Connect()
        {
            try
            {
                _solidWorks = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
                IsConnected = true;
                EnsureVisible(); // the user must ALWAYS see what the automation does
                return true;
            }
            catch (COMException)
            {
                IsConnected = false;
                return false;
            }
        }

        // Force the attached SolidWorks instance to be on-screen and user-controllable.
        // GetActiveObject binds to whatever instance is first in the COM Running Object Table — which
        // can be a headless/COM-launched instance with NO window (Visible=false). If we silently drove
        // that one, the user would watch a different window and see nothing happen (observed live: two
        // SLDWORKS processes, the older one window-less). Policy (for now): every instance we touch is
        // made visible. Best-effort — a visibility failure must never break the attach or a tool call.
        private void EnsureVisible()
        {
            try
            {
                if (_solidWorks == null) return;
                if (!_solidWorks.Visible) _solidWorks.Visible = true;
                _solidWorks.UserControl = true;
            }
            catch { /* visibility is best-effort; never fail over it */ }
        }

        public T FindOpenDocument<T>() where T : class
        {
            if (_solidWorks == null) return null;
            try
            {
                object[] docs = _solidWorks.GetDocuments() as object[];
                if (docs != null)
                {
                    foreach (var d in docs)
                    {
                        if (d is T target) return target;
                    }
                }
            }
            catch { }
            return null;
        }

        public IModelDoc2 FindOpenPartOrAssembly()
        {
            var p = FindOpenDocument<IPartDoc>() as IModelDoc2;
            if (p != null) return p;
            return FindOpenDocument<IAssemblyDoc>() as IModelDoc2;
        }

        private bool EnsureConnected()
        {
            if (_solidWorks != null)
            {
                try
                {
                    _solidWorks.RevisionNumber();
                    return true;
                }
                catch
                {
                    _solidWorks = null;
                    IsConnected = false;
                }
            }

            if (Connect()) return true;

            var running = SolidWorksProcesses();
            if (running.Count > 0)
            {
                if (RetryAttach(TimeSpan.FromSeconds(30))) return true;
            }
            else
            {
                // SolidWorks is not running (e.g. terminated or cleanly recycled by ProcessRecycler).
                // Launch a fresh instance to ensure continuous batch operation across 30,000+ files!
                try
                {
                    ExecLog.Write("[EnsureConnected] SolidWorks not running. Launching new instance for batch continuation...");
                    var progType = Type.GetTypeFromProgID("SldWorks.Application");
                    if (progType != null)
                    {
                        _solidWorks = (ISldWorks)Activator.CreateInstance(progType);
                        EnsureVisible();
                        if (RetryAttach(TimeSpan.FromSeconds(90)))
                        {
                            IsConnected = true;
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ExecLog.Write($"[EnsureConnected] Failed to launch SolidWorks: {ex.Message}");
                }
            }

            return false;
        }

        // The PIDs of every running SolidWorks, regardless of whether COM can see them. This is the
        // check that makes single-instance discipline possible: the ROT and the process list disagree
        // exactly in the cases that used to spawn a duplicate (KNOWN-LIMITATIONS #14).
        private static List<int> SolidWorksProcesses()
        {
            try
            {
                return Process.GetProcessesByName("SLDWORKS").Select(p => p.Id).ToList();
            }
            catch
            {
                return new List<int>(); // never let a diagnostic break the lifecycle path
            }
        }

        // Poll for a FRESH ROT attach until `timeout`. Used both after launching (the app needs time
        // before it is usable) and instead of launching when a process already exists but has not
        // registered yet. Gates on a real call, not mere registration, because that is what the next
        // tool call will do. Sets `_solidWorks` on success so the caller can read doc/version off it.
        private bool RetryAttach(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                try
                {
                    var probe = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
                    probe.RevisionNumber(); // confirms responsive, not just registered
                    _solidWorks = probe;
                    EnsureVisible();
                    return true;
                }
                catch
                {
                    if (DateTime.UtcNow >= deadline) return false;
                    System.Threading.Thread.Sleep(1000); // still spinning up / COM server busy
                }
            }
        }

        // Health probe (P0.6): attempts a fresh COM attach and reports whether SolidWorks is reachable
        // plus the active document title. Must run on the STA thread (it touches COM via Connect()).
        // Returns a dictionary so no new compiled type is needed (old-style csproj).
        public Dictionary<string, object> GetHealthInfo()
        {
            var result = new Dictionary<string, object>();
            bool attached = Connect();
            result["comAttached"] = attached;
            if (attached)
            {
                try
                {
                    var d = _solidWorks.IActiveDoc2 as IModelDoc2;
                    result["activeDocument"] = d != null ? d.GetTitle() : null;
                }
                catch { /* attached but couldn't read the active doc — leave it unset */ }
            }
            // Duplicate/hidden-instance diagnostic (KNOWN-LIMITATIONS #14). A count > 1 is the
            // condition under which automation can drive an instance the user is not watching, and a
            // count > 0 while comAttached is false is the ROT-vs-process disagreement that used to
            // spawn another. windowless>0 names the specific bad case: an instance with no window.
            try
            {
                var procs = Process.GetProcessesByName("SLDWORKS");
                result["solidworksProcessCount"] = procs.Length;
                result["solidworksPids"] = procs.Select(p => p.Id).ToArray();
                result["solidworksWindowless"] = procs.Count(p => p.MainWindowHandle == IntPtr.Zero);
            }
            catch { /* diagnostic only — never fail /health over it */ }
            return result;
        }

        // Lifecycle bootstrap (ensure_ready): unlike GetHealthInfo (read-only probe), this LAUNCHES
        // SolidWorks via COM when it isn't already running, then waits until a fresh ROT attach
        // succeeds — because every real tool call re-attaches on a new service instance via
        // GetActiveObject, that is the exact readiness gate that matters. Does NOT open/create any
        // document (assembly/drawing contexts come later — opening a part here would be wrong).
        // Must run on the STA thread (touches COM). Returns a plain dictionary (old-style csproj).
        public Dictionary<string, object> EnsureReady()
        {
            var result = new Dictionary<string, object>();
            bool launched = false;
            bool attached = Connect();

            if (!attached)
            {
                // SINGLE-INSTANCE DISCIPLINE (KNOWN-LIMITATIONS #14, 2026-07-30). A failed attach does
                // NOT mean SolidWorks is absent: Marshal.GetActiveObject only sees the COM Running
                // Object Table, and a RUNNING instance can be missing from our view of it — it is still
                // starting up and has not registered yet, or it runs at a different integrity level
                // (started elevated while this server is not, or vice versa), or it is wedged in a modal
                // dialog. Launching in those cases produced a SECOND SLDWORKS.exe, which is what the
                // user actually sees. So: only CreateInstance when NO SolidWorks process exists at all.
                var running = SolidWorksProcesses();
                if (running.Count > 0)
                {
                    // One (or more) IS running but the ROT attach failed. Retry the attach — the common
                    // benign case is an instance mid-startup — and NEVER launch another.
                    attached = RetryAttach(TimeSpan.FromSeconds(30));
                    IsConnected = attached;
                    if (!attached)
                    {
                        result["comAttached"] = false;
                        result["swLaunched"] = false;
                        result["solidworksProcessCount"] = running.Count;
                        result["ensureError"] =
                            "SolidWorks IS running (PID " + string.Join(", ", running.Select(p => p.ToString()).ToArray()) +
                            ") but COM attach via the Running Object Table failed after 30s. A second instance was " +
                            "deliberately NOT launched (KNOWN-LIMITATIONS #14). Most likely causes, in order: " +
                            "(1) SolidWorks is elevated/'Run as administrator' while this execution server is not " +
                            "(or the reverse) — the ROT is not shared across integrity levels, so run both the same way; " +
                            "(2) SolidWorks is still loading, or is blocked on a modal dialog / license prompt — clear it and retry; " +
                            "(3) the instance never registered in the ROT. Check: Get-Process SLDWORKS | Select Id, MainWindowHandle, MainWindowTitle";
                        return result;
                    }
                }
                else
                {
                    // Genuinely nothing running — start it out-of-process via COM and make it visible.
                    try
                    {
                        var progType = Type.GetTypeFromProgID("SldWorks.Application");
                        if (progType == null)
                            throw new Exception("SldWorks.Application ProgID is not registered. Is SolidWorks installed?");

                        _solidWorks = (ISldWorks)Activator.CreateInstance(progType);
                        EnsureVisible(); // visible + user-controllable from the moment it launches
                        launched = true;

                        // CreateInstance returns before the app is usable. Gate on a FRESH ROT attach
                        // (Marshal.GetActiveObject) succeeding + a responsive call — that's what the next
                        // tool call will do. Wait up to 90s (adapter ENSURE_TIMEOUT is 120s).
                        attached = RetryAttach(TimeSpan.FromSeconds(90));
                        IsConnected = attached;
                    }
                    catch (Exception ex)
                    {
                        result["comAttached"] = false;
                        result["swLaunched"] = false;
                        result["launchError"] = ex.Message;
                        return result;
                    }
                }
            }

            result["comAttached"] = attached;
            result["swLaunched"] = launched;
            if (attached)
            {
                try
                {
                    var d = _solidWorks.IActiveDoc2 as IModelDoc2;
                    result["activeDocument"] = d != null ? d.GetTitle() : null;
                    result["swVersion"] = _solidWorks.RevisionNumber();
                }
                catch { /* attached but couldn't read doc/version — leave unset */ }
            }
            else if (!result.ContainsKey("launchError"))
            {
                // Launched but never became responsive within the wait window.
                result["launchError"] = "SolidWorks was launched but did not become responsive within the timeout.";
            }
            return result;
        }

        public ExecutionResponse OpenNewPart(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                string templatePath = p != null ? p.Value<string>("template_path") : null;

                if (string.IsNullOrEmpty(templatePath))
                {
                    if (!string.IsNullOrEmpty(_cachedPartTemplate) && System.IO.File.Exists(_cachedPartTemplate))
                    {
                        templatePath = _cachedPartTemplate;
                    }
                    else
                    {
                        templatePath = _solidWorks.GetUserPreferenceStringValue(
                            (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);

                        if (string.IsNullOrEmpty(templatePath) || !System.IO.File.Exists(templatePath))
                        {
                            string templatesDir = System.IO.Path.Combine(
                                System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
                                "SolidWorks");
                            if (System.IO.Directory.Exists(templatesDir))
                            {
                                var found = System.IO.Directory.GetFiles(templatesDir, "*.prtdot",
                                    System.IO.SearchOption.AllDirectories);
                                if (found.Length > 0)
                                    templatePath = found[0];
                            }
                        }

                        if (!string.IsNullOrEmpty(templatePath) && System.IO.File.Exists(templatePath))
                            _cachedPartTemplate = templatePath;
                    }
                }

                if (string.IsNullOrEmpty(templatePath) || !System.IO.File.Exists(templatePath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "TEMPLATE_NOT_FOUND",
                        "No part template (.prtdot) found. Set a default template in SolidWorks → Tools → Options → Default Templates.");

                object doc = _solidWorks.NewDocument(templatePath, 0, 0, 0);
                var modelDoc = doc as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "DOCUMENT_CREATION_FAILED",
                        $"SolidWorks NewDocument returned null. Template used: '{templatePath}'. Ensure SolidWorks → Tools → Options → Default Templates has a valid part template configured.");

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = null,
                        Features = new List<string>(),
                        Dimensions = new List<string>()
                    },
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        // open_document — open an EXISTING file from disk (the counterpart to open_new_part, which only
        // makes a BLANK doc). Native .sldprt/.sldasm/.slddrw open directly; foreign .ipt/.CATPart/STEP/etc.
        // import as a PART via SolidWorks 3D Interconnect / translators (install/license dependent — a
        // failure returns a clear OPEN_FAILED so the caller can defer that sample, per the drawing plan).
        // Bumps state_version (a new active document is established), same effect class as open_new_part.
        public ExecutionResponse OpenDocument(ToolRequest request)
        {
            if (!ValidateStandardGuards(request, out var failed))
                return failed;

            try
            {
                var p = request.Params as JObject;
                string filePath = p != null ? p.Value<string>("file_path") : null;
                if (string.IsNullOrEmpty(filePath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "file_path is required.");
                if (!System.IO.File.Exists(filePath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "FILE_NOT_FOUND", $"No file at '{filePath}'.");

                // Document type from the extension. swconst values inlined as constants (ADR-018).
                string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                int docType;
                switch (ext)
                {
                    case ".sldasm": docType = (int)swDocumentTypes_e.swDocASSEMBLY; break;
                    case ".slddrw": docType = (int)swDocumentTypes_e.swDocDRAWING; break;
                    // .sldprt AND foreign part formats (.ipt/.CATPart/.step/.stp/.iges/.igs/.x_t/.x_b/...)
                    // open as a PART (3D Interconnect imports the foreign ones).
                    default: docType = (int)swDocumentTypes_e.swDocPART; break;
                }

                int errors = 0, warnings = 0;
                var modelDoc = _solidWorks.OpenDoc6(filePath, docType,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as IModelDoc2;

                // FOREIGN-FORMAT staged fallback (2026-07-09, proven live): with 3D Interconnect OFF,
                // OpenDoc6 does NOT import foreign files — it returns null with the misleading
                // errors=2097152 (swFileRequiresRepairError; observed on both .step and NX .prt).
                // Stage 2: LoadFile4 = the CLASSIC translator import (STEP/IGES; content decides
                // part vs assembly). Stage 3: NX/Creo/CATIA native formats have no classic
                // translator — they REQUIRE Interconnect, so enable it (documented prerequisite,
                // swMultiCAD_Enable3DInterconnect=691) and retry OpenDoc6 once; left ON afterwards
                // (the steady state for mixed-CAD sample folders).
                bool foreign = ext != ".sldprt" && ext != ".sldasm" && ext != ".slddrw";
                string openPath = "OpenDoc6";
                // as_assembly=true (foreign only): the file is an ASSEMBLY STEP/etc. The classic
                // translator (LoadFile4) imports an assembly STEP as a MULTIBODY PART (proven live
                // 2026-07-09 on Fixture Assembly.step) — only 3D Interconnect maps it to a real
                // assembly with components. Enable the toggle just for this open and RESTORE it
                // after, so foreign PART opens elsewhere keep their proven classic-import behavior.
                bool asAssembly = p.Value<bool?>("as_assembly") ?? false;
                if (modelDoc == null && foreign && asAssembly)
                {
                    bool prior = _solidWorks.GetUserPreferenceToggle(691); // swMultiCAD_Enable3DInterconnect
                    bool onlyParts = _solidWorks.GetUserPreferenceToggle(758); // swMultiCAD_ApplyOnlyToParts — excludes assemblies from Interconnect when ON
                    try
                    {
                        if (!prior) _solidWorks.SetUserPreferenceToggle(691, true);
                        if (onlyParts) _solidWorks.SetUserPreferenceToggle(758, false);
                        errors = 0; warnings = 0;
                        modelDoc = _solidWorks.OpenDoc6(filePath, (int)swDocumentTypes_e.swDocASSEMBLY,
                            (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as IModelDoc2;
                        openPath = "OpenDoc6+3DInterconnect(as_assembly)";
                        ExecLog.Write($"open_document(as_assembly): prior691={prior} onlyParts758={onlyParts} errors={errors} warnings={warnings} null={modelDoc == null}");
                    }
                    finally
                    {
                        if (!prior) _solidWorks.SetUserPreferenceToggle(691, false);
                        if (onlyParts) _solidWorks.SetUserPreferenceToggle(758, true);
                    }
                }
                if (modelDoc == null && foreign)
                {
                    int lfErrors = 0;
                    modelDoc = _solidWorks.LoadFile4(filePath, "r", null, ref lfErrors) as IModelDoc2;
                    openPath = "LoadFile4";
                    if (modelDoc == null && !_solidWorks.GetUserPreferenceToggle(691))
                    {
                        _solidWorks.SetUserPreferenceToggle(691, true);
                        ExecLog.Write("open_document: enabled 3D Interconnect (toggle 691) for foreign-format import");
                        errors = 0; warnings = 0;
                        modelDoc = _solidWorks.OpenDoc6(filePath, docType,
                            (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as IModelDoc2;
                        openPath = "OpenDoc6+3DInterconnect";
                    }
                }

                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "OPEN_FAILED",
                        $"Could not open '{filePath}' (OpenDoc6 errors={errors}, then LoadFile4 also null for foreign formats). " +
                        "errors=2097152 is swFileRequiresRepairError — for foreign files this usually means no usable " +
                        "translator; convert to STEP or defer this sample.");
                ExecLog.Write($"open_document: '{System.IO.Path.GetFileName(filePath)}' via {openPath}");

                string typeName = modelDoc is IDrawingDoc ? "DRAWING"
                                : modelDoc is IAssemblyDoc ? "ASSEMBLY" : "PART";

                // ALREADY-OPEN gotcha (the 2026-07-05 PENDING bug, fixed 2026-07-07): OpenDoc6 on an
                // already-open file returns its IModelDoc2 but does NOT activate it — the previously
                // active document silently stays active, so follow-up reads (analyze/save_analysis)
                // hit the WRONG doc. Explicitly activate what we just opened, then verify.
                int actErr = 0;
                _solidWorks.ActivateDoc3(modelDoc.GetTitle(), false, 1 /* swDontRebuildActiveDoc */, ref actErr);
                var nowActive = _solidWorks.IActiveDoc2 as IModelDoc2;
                // Title normalization: an imported doc's title can gain/lose its extension between
                // reads ('Butterfly_valve' vs 'Butterfly_valve.prt' — observed live on an NX
                // import). Compare extension-stripped, case-insensitive.
                Func<string, string> baseTitle = t =>
                    (System.IO.Path.GetFileNameWithoutExtension(t ?? "") ?? "").ToLowerInvariant();
                if (nowActive == null || baseTitle(nowActive.GetTitle()) != baseTitle(modelDoc.GetTitle()))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "ACTIVATE_FAILED",
                        $"Opened '{modelDoc.GetTitle()}' but could not make it the ACTIVE document " +
                        $"(active is '{nowActive?.GetTitle()}').");

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = typeName,
                        ActiveSketch = null,
                        Features = new List<string> { System.IO.Path.GetFileName(filePath) },
                        Dimensions = new List<string>()
                    },
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        // Name of the LAST feature in the tree (creation-order detection for void-returning
        // feature APIs like InsertRib). Null-safe.
        private static string LastFeatureName(IModelDoc2 modelDoc)
        {
            try
            {
                object[] all = modelDoc.FeatureManager.GetFeatures(true) as object[];
                if (all == null || all.Length == 0) return null;
                return (all[all.Length - 1] as IFeature)?.Name;
            }
            catch { return null; }
        }

        public ExecutionResponse VerifyState(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                var featureNames = new List<string>();
                object[] rawFeatures = modelDoc.FeatureManager.GetFeatures(true) as object[];
                if (rawFeatures != null)
                {
                    foreach (var f in rawFeatures)
                    {
                        var feat = f as IFeature;
                        if (feat != null)
                            featureNames.Add(feat.Name);
                    }
                }

                var activeSketch = modelDoc.SketchManager.ActiveSketch;

                // VerifyState does NOT increment StateVersion and does NOT call RegisterCompleted
                return new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = modelDoc is IDrawingDoc ? "DRAWING" : (modelDoc is IAssemblyDoc ? "ASSEMBLY" : "PART"),
                        ActiveSketch = activeSketch != null ? (activeSketch as IFeature)?.Name : null,
                        Features = featureNames,
                        Dimensions = new List<string>()
                    },
                    Error = null
                };
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        public ExecutionResponse CloseDocument(ToolRequest request)
        {
            if (!ValidateStandardGuards(request, out var failed))
                return failed;

            try
            {
                var p = request.Params as JObject;
                bool save = p != null && (p.Value<bool?>("save") ?? false);
                bool closeAll = p != null && (p.Value<bool?>("close_all") ?? false);

                if (closeAll)
                {
                    // Close EVERYTHING, including invisibly-loaded component docs that a
                    // per-active-doc close loop can never reach (they are never active).
                    _solidWorks.CloseAllDocuments(true);
                    ProcessRecycler.Instance.NotifyDocumentProcessed();
                    ComScope.CollectGarbage();
                }
                else
                {
                    var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                    if (modelDoc == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                    string docTitle = modelDoc.GetTitle();
                    if (save)
                    {
                        int saveErrors = 0, saveWarnings = 0;
                        modelDoc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings);
                    }
                    _solidWorks.CloseDoc(docTitle);
                    ComScope.Release(modelDoc);
                    modelDoc = null;

                    ProcessRecycler.Instance.NotifyDocumentProcessed();
                    ComScope.CollectGarbage();
                }

                if (ProcessRecycler.Instance.ShouldRecycle(_solidWorks, out var recycleReason))
                {
                    ExecLog.Write($"[CloseDocument] Auto-recycling SolidWorks: {recycleReason}");
                    ProcessRecycler.Instance.Recycle(ref _solidWorks);
                }

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = null,
                        DocumentType = null,
                        ActiveSketch = null,
                        Features = new List<string>(),
                        Dimensions = new List<string>()
                    },
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        public ExecutionResponse SaveDocument(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                string filePath = p?.Value<string>("file_path");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                int saveErrors = 0, saveWarnings = 0;
                bool success;

                if (string.IsNullOrEmpty(filePath))
                {
                    // Save in place — requires the document to already have a path on disk.
                    if (string.IsNullOrEmpty(modelDoc.GetPathName()))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER", "Document has never been saved; file_path is required for the first save (full path including .sldprt/.sldasm/.slddrw extension).");
                    success = modelDoc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings);
                }
                else
                {
                    // SaveAs to an explicit path. Ensure output directory exists.
                    string dir = System.IO.Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                        System.IO.Directory.CreateDirectory(dir);

                    success = modelDoc.Extension.SaveAs3(filePath,
                        (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                        null, null,
                        ref saveErrors, ref saveWarnings);
                }

                if (!success)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SAVE_FAILED", $"Save failed. Errors: {saveErrors}, Warnings: {saveWarnings}. Ensure the output path is writable and the extension matches the document type (.sldprt/.sldasm/.slddrw).");

                // Saving does not change CAD geometry — state_version is unchanged (same as export).
                string savedPath = modelDoc.GetPathName();
                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = modelDoc is IDrawingDoc ? "DRAWING" : (modelDoc is IAssemblyDoc ? "ASSEMBLY" : "PART"),
                        ActiveSketch = null,
                        Features = new List<string> { savedPath },
                        Dimensions = new List<string>()
                    },
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response, incrementsState: false);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        public ExecutionResponse ExportDocument(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                string format = p?.Value<string>("format")?.ToUpperInvariant();
                string filePath = p?.Value<string>("file_path");

                if (string.IsNullOrEmpty(format))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "format is required: STEP, IGES, STL, PDF, DWG, DXF, PNG (any other extension SolidWorks understands also works - the format is taken from the extension).");
                if (string.IsNullOrEmpty(filePath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "file_path is required (full output path including filename and extension).");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                bool isDrawing = modelDoc is IDrawingDoc;

                // DWG/DXF require a drawing document
                if ((format == "DWG" || format == "DXF") && !isDrawing)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", $"Export format '{format}' requires an active drawing document (not a part). Call create_drawing first.");

                // SolidWorks infers the format from the extension; its IGES translator only recognizes
                // .igs (not .iges → SaveAs error 256). Correct it so an explicit .iges path still works.
                if (format == "IGES" && !filePath.ToLowerInvariant().EndsWith(".igs"))
                    filePath = System.IO.Path.ChangeExtension(filePath, "igs");

                // Ensure output directory exists
                string dir = System.IO.Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                int exportErrors = 0;
                int exportWarnings = 0;
                bool success;
                if (isDrawing)
                {
                    try { modelDoc.ViewZoomtofit2(); } catch { }
                    try { modelDoc.GraphicsRedraw2(); } catch { }
                }

                if (format == "PDF")
                {
                    // PDF requires ExportPdfData options object
                    var exportData = _solidWorks.GetExportFileData(
                        (int)swExportDataFileType_e.swExportPdfData) as ExportPdfData;
                    try
                    {
                        success = modelDoc.Extension.SaveAs3(filePath,
                            (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                            (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                            exportData, null,
                            ref exportErrors, ref exportWarnings);
                    }
                    finally
                    {
                        ComScope.Release(exportData);
                    }
                }
                else
                {
                    success = modelDoc.Extension.SaveAs3(filePath,
                        (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                        null, null,
                        ref exportErrors, ref exportWarnings);
                }

                if (!success)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "EXPORT_FAILED", $"SaveAs3 failed for format '{format}'. Errors: {exportErrors}, Warnings: {exportWarnings}. Ensure the output path is writable and the format matches the document type.");

                // ExportDocument does NOT increment state_version — no CAD state change
                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = isDrawing ? "DRAWING" : "PART",
                        ActiveSketch = null,
                        Features = new List<string> { filePath },
                        Dimensions = new List<string>()
                    },
                    Error = null
                };

                // Register as completed for idempotency but with same state_version
                _guard.RegisterCompleted(request.OperationId, response, incrementsState: false);
                ProcessRecycler.Instance.NotifyDocumentProcessed();
                ComScope.CollectGarbage();
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        public ExecutionResponse BatchExport(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                string filePathBase = p?.Value<string>("file_path_base");
                string formatsJson = p?.Value<string>("formats_json");

                if (string.IsNullOrEmpty(filePathBase))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "file_path_base is required (output path without extension, e.g. 'C:/output/mypart').");
                if (string.IsNullOrEmpty(formatsJson))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "formats_json is required (JSON array of format strings, e.g. '[\"STEP\",\"STL\"]').");

                JArray formatArray;
                try { formatArray = JArray.Parse(formatsJson); }
                catch { return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", "formats_json must be a valid JSON array of format strings."); }

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                bool isDrawing = modelDoc is IDrawingDoc;

                string dir = System.IO.Path.GetDirectoryName(filePathBase);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                var exported = new List<string>();
                var failed = new List<string>();

                bool prevTree = true;
                try { prevTree = modelDoc.FeatureManager.EnableFeatureTree; modelDoc.FeatureManager.EnableFeatureTree = false; } catch { }

                try
                {
                    foreach (var fToken in formatArray)
                    {
                        string fmt = fToken.Value<string>()?.ToUpperInvariant();
                        if (string.IsNullOrEmpty(fmt)) continue;

                        if ((fmt == "DWG" || fmt == "DXF") && !isDrawing)
                        {
                            failed.Add($"{fmt}:WRONG_DOCUMENT_TYPE");
                            continue;
                        }

                        string ext = fmt == "IGES" ? "igs" : fmt.ToLowerInvariant();
                        string outPath = filePathBase + "." + ext;

                        int exportErrors = 0;
                        int exportWarnings = 0;
                        bool success;

                        if (fmt == "PDF")
                        {
                            var exportData = _solidWorks.GetExportFileData(
                                (int)swExportDataFileType_e.swExportPdfData) as ExportPdfData;
                            try
                            {
                                success = modelDoc.Extension.SaveAs3(outPath,
                                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                                    exportData, null, ref exportErrors, ref exportWarnings);
                            }
                            finally
                            {
                                ComScope.Release(exportData);
                            }
                        }
                        else
                        {
                            success = modelDoc.Extension.SaveAs3(outPath,
                                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                                null, null, ref exportErrors, ref exportWarnings);
                        }

                        if (success) exported.Add(outPath);
                        else failed.Add($"{fmt}:errors={exportErrors}");
                    }
                }
                finally
                {
                    try { modelDoc.FeatureManager.EnableFeatureTree = prevTree; } catch { }
                }

                if (exported.Count == 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "BATCH_EXPORT_FAILED", $"All exports failed. Details: {string.Join(", ", failed)}");

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = isDrawing ? "DRAWING" : "PART",
                        ActiveSketch = null,
                        Features = exported,
                        Dimensions = failed.Count > 0 ? new List<string> { $"partial_failures: {string.Join(", ", failed)}" } : new List<string>()
                    },
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response, incrementsState: false);
                ProcessRecycler.Instance.NotifyDocumentProcessed();
                ComScope.CollectGarbage();
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        // -----------------------------------------------------------------------
        // activate_document — switch the active SolidWorks document by title.
        // Lifecycle-ish: changes which open doc subsequent ops target, not geometry. Like ensure_ready it
        // does NOT check/bump state_version (it's process-global and the activated doc's geometry is
        // unchanged). Lets a caller compare against another open part without OS window hacks.
        // -----------------------------------------------------------------------
        public ExecutionResponse ActivateDocument(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                string title = p?.Value<string>("title");
                if (string.IsNullOrEmpty(title))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "title is required (the open document's title, e.g. 'gear' or 'gear.SLDPRT').");

                int errors = 0;
                // 3rd arg = swRebuildOnActivation_e; 1 = swDontRebuildActiveDoc (literal, don't load swconst).
                var doc = _solidWorks.ActivateDoc3(title, false, 1, ref errors) as IModelDoc2;
                if (doc == null &&
                    !title.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase) &&
                    !title.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) &&
                    !title.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase))
                {
                    // Retry with the part extension if the bare title didn't resolve.
                    doc = _solidWorks.ActivateDoc3(title + ".SLDPRT", false, 1, ref errors) as IModelDoc2;
                }
                if (doc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "DOCUMENT_NOT_FOUND",
                        $"Could not activate '{title}' (errors={errors}). Pass the exact title of an OPEN document.");

                ExecLog.Write($"activate_document: '{title}' -> active '{doc.GetTitle()}'");
                return new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = doc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = null,
                        Features = new List<string>(),
                        Dimensions = new List<string>()
                    },
                    Error = null
                };
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        /// <summary>
        /// Language-independent default-plane selection. SolidWorks localizes the default
        /// plane names (e.g. "Piano frontale" on an Italian install), so SelectByID2
        /// by the English name fails on non-English installs. We first try the given name
        /// (English installs keep working), then fall back to mapping Front/Top/Right to the
        /// first three RefPlane features in tree order (the default planes), selecting the
        /// feature object directly — no name involved.
        /// </summary>
        private bool SelectPlaneFlexible(IModelDoc2 modelDoc, string planeName, bool append, int mark)
        {
            // 1) Try by name (works on English installs, and for explicitly-named planes).
            if (modelDoc.Extension.SelectByID2(planeName, "PLANE", 0, 0, 0, append, mark, null, 0))
                return true;

            // 2) Map a canonical default-plane name to its ordinal among the default planes.
            string n = (planeName ?? string.Empty).ToLowerInvariant();
            int ordinal;
            if (n.Contains("front")) ordinal = 0;
            else if (n.Contains("top")) ordinal = 1;
            else if (n.Contains("right")) ordinal = 2;
            else return false; // not a default plane name we can resolve language-independently

            // 3) Walk the feature tree, collect RefPlane features in order; the first three are
            //    the default planes (Front, Top, Right). Select the Nth directly.
            var features = modelDoc.FeatureManager.GetFeatures(true) as object[];
            if (features == null) return false;

            int seen = 0;
            foreach (var obj in features)
            {
                var feat = obj as IFeature;
                if (feat == null) continue;
                if (feat.GetTypeName2() != "RefPlane") continue;
                if (seen == ordinal)
                {
                    if (!append) modelDoc.ClearSelection2(true);
                    return feat.Select2(append, mark);
                }
                seen++;
            }
            return false;
        }

        // Enumerate every solid body's edges with start/end/MIDPOINT 3D coords (+ length). The midpoint
        // is the natural pick-point for coordinate-based selection (edge_flange ex/ey/ez), so the caller
        // never has to guess where an edge is or reverse-derive the extrude direction. Best-effort per edge.
        // One FeatureCut4 invocation with the full (verbose) parameter list factored out, so the cut path
        // can try a direction / normalCut combination cleanly and retry the flipped direction.
        // APPEND-select the i-th solid-body face with a selection Mark — the up-to-surface end-condition
        // reference for FeatureExtrusion3/FeatureCut4. Same GetBodies2(swSolidBody,true) → GetFaces()
        // enumeration analyze_model(faces) reports and create_sketch(on_face, face_index) walks, so the
        // caller's face index means the same thing everywhere. Mark: per the FeatureExtrusion3 remarks
        // (local sldworksapi.chm) "End condition reference entity = Mark 1" (profile 0, direction edge 16,
        // start reference 32, bodies 8) — matches CodeStack's working up-to-surface example.
        private bool SelectFaceByIndexAppend(IModelDoc2 modelDoc, int faceIndex, int mark)
        {
            var partDoc = modelDoc as IPartDoc;
            var allFaces = new List<IFace2>();
            object[] bodies = partDoc?.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            if (bodies != null)
                foreach (var b in bodies)
                {
                    var body = b as IBody2;
                    if (body == null) continue;
                    object[] fs = body.GetFaces() as object[];
                    if (fs == null) continue;
                    foreach (var f in fs) { var fa = f as IFace2; if (fa != null) allFaces.Add(fa); }
                }
            if (faceIndex < 0 || faceIndex >= allFaces.Count) return false;
            var ent = allFaces[faceIndex] as IEntity;
            if (ent == null) return false;
            var selData = (modelDoc.SelectionManager as ISelectionMgr)?.CreateSelectData();
            if (selData == null) return false;
            selData.Mark = mark;
            return ent.Select4(true, selData);
        }

        // Locate a feature by its exact tree name, walking top-level features AND one level of
        // sub-features (folder contents) — same traversal as analyze's tree walk, so a feature
        // analyze reports can always be found here. Collects every visited name into namesOut for a
        // helpful "available features" error list. Returns null if not found.
        private IFeature FindFeatureByName(IModelDoc2 modelDoc, string name, List<string> namesOut)
        {
            IFeature found = null;
            var feat = modelDoc.FirstFeature() as IFeature;
            int guard = 0;
            while (feat != null && guard++ < 5000)
            {
                if (namesOut != null) namesOut.Add(feat.Name);
                if (found == null && feat.Name == name) found = feat;

                var sub = feat.GetFirstSubFeature() as IFeature;
                int subGuard = 0;
                while (sub != null && subGuard++ < 2000)
                {
                    if (namesOut != null) namesOut.Add(sub.Name);
                    if (found == null && sub.Name == name) found = sub;
                    sub = sub.GetNextSubFeature() as IFeature;
                }

                feat = feat.GetNextFeature() as IFeature;
            }
            return found;
        }

        protected ExecutionResponse BuildFailed(string operationId, int stateVersion, string code, string message)
        {
            return new ExecutionResponse
            {
                OperationId = operationId,
                Status = "FAILED",
                Verified = false,
                StateVersion = stateVersion,
                CadState = null,
                Error = new ExecutionError
                {
                    Code = code,
                    Message = message
                }
            };
        }
    }
}

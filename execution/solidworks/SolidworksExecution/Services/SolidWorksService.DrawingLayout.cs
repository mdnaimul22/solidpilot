using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // SolidWorksService partial: PRINTABLE-DRAWING LAYOUT + document units.
    //
    // The baseline tool set could create a drawing and add views/dimensions, but it could not
    // FINISH a print-ready packet. The gaps, closed here:
    //   set_document_units  - make dimension text read in mm (not the template's units)
    //   set_drawing_sheet   - paper size / orientation / activate a sheet
    //   add_drawing_sheet   - a second page (one part or assembly per page)
    //   add_drawing_note    - free text: title block, page number, "Measurement : Millimeter",
    //                         optionally with a leader to an edge/vertex/face (assembly labels)
    //   add_balloon         - one BOM balloon attached to the component under a point
    //   auto_balloon        - every component in a view ballooned at once (BOM legend)
    //
    // Kept in one file, deliberately: these six belong to one workflow and one place to look.
    //
    // swconst values are INLINED ints (ADR-018): the swconst DLL is not copied to bin\Debug, so
    // only compile-time constants may be used - never swconst types at runtime.
    public partial class SolidWorksService
    {
        // swUserPreferenceIntegerValue_e
        private const int SW_PREF_UNIT_SYSTEM = 263;
        private const int SW_PREF_UNITS_LINEAR = 47;
        private const int SW_PREF_UNITS_DECIMALS = 49;
        // swUnitSystem_e
        private const int SW_UNIT_SYSTEM_MKS = 2;
        private const int SW_UNIT_SYSTEM_IPS = 3;
        private const int SW_UNIT_SYSTEM_CUSTOM = 4;
        private const int SW_UNIT_SYSTEM_MMGS = 5;
        // swDwgPaperSizes_e (swDwgTemplates_e shares the numbering)
        private const int SW_PAPER_A4 = 6;
        private const int SW_PAPER_A4_VERTICAL = 7;
        private const int SW_PAPER_A3 = 8;
        private const int SW_PAPER_A2 = 9;
        private const int SW_PAPER_A1 = 10;
        private const int SW_PAPER_A0 = 11;
        private const int SW_PAPER_USER = 12;
        // swDwgTemplates_e
        private const int SW_TEMPLATE_NONE = 13;
        // swTextJustification_e
        private const int SW_JUSTIFY_LEFT = 1;
        private const int SW_JUSTIFY_CENTER = 2;
        private const int SW_JUSTIFY_RIGHT = 3;
        // swLeaderSide_e
        private const int SW_LEADER_SMART = 0;
        private const int SW_LEADER_LEFT = 1;
        private const int SW_LEADER_RIGHT = 2;
        // swArrowStyle_e
        private const int SW_ARROW_OPEN = 0;
        // swBalloonTextContent_e
        private const int SW_BALLOON_TEXT_CUSTOM = 0;
        private const int SW_BALLOON_TEXT_ITEM_NUMBER = 1;

        // =====================================================================
        // set_document_units
        // =====================================================================
        public ExecutionResponse SetDocumentUnits(ToolRequest request)
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
                string linearIn = p?.Value<string>("linear") ?? "mm";
                int decimals = p?.Value<int?>("decimals") ?? 2;
                if (decimals < 0) decimals = 0;
                if (decimals > 6) decimals = 6;

                int unit, unitSystem;
                if (!MapLinearUnit(linearIn, out unit, out unitSystem))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "UNSUPPORTED_UNIT",
                        "linear must be one of: mm, cm, m, in, ft, ft-in, micron, mil, uin.");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                // Set the unit system first, then the concrete linear unit (which switches the
                // document to Custom units), then the decimal places - this order is what the
                // SolidWorks UI itself does.
                ApplyLinearUnits(modelDoc, unit, unitSystem, decimals);

                // Read back what the document actually reports - ground truth, not the input.
                var ext = modelDoc.Extension;
                int readUnit = ext.GetUserPreferenceInteger(SW_PREF_UNITS_LINEAR, 0);
                int readDecimals = ext.GetUserPreferenceInteger(SW_PREF_UNITS_DECIMALS, 0);

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
                        DocumentType = DocumentKindName(modelDoc),
                        ActiveSketch = null,
                        Features = new List<string>(),
                        Dimensions = new List<string>()
                    },
                    ResultGeometry = new JObject
                    {
                        ["linear"] = UnitName(readUnit),
                        ["decimals"] = readDecimals,
                        ["unit_system"] = unitSystem
                    },
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.Message);
            }
        }

        // =====================================================================
        // automatic view placement (no overlap, third-angle alignment)
        // =====================================================================
        private void PlaceViewWithoutOverlap(IDrawingDoc drawingDoc, IView view, bool searchEvenIfLegal)
        {
            const double gap = 0.012;
            const double margin = 0.015;

            double sheetW = 0.420, sheetH = 0.297;
            var sheet = drawingDoc.GetCurrentSheet() as ISheet;
            if (sheet != null) sheet.GetSize(ref sheetW, ref sheetH);

            var occupied = new List<double[]>();
            IView reference = null;
            double largest = 0;
            string selfName = view.Name;
            var v = drawingDoc.GetFirstView() as IView;
            while (v != null)
            {
                IView next = null;
                try { next = v.GetNextView() as IView; } catch { }
                if (v.Name != selfName && v.Type != 1)
                {
                    var b = v.GetOutline() as double[];
                    if (b != null && b.Length >= 4)
                    {
                        occupied.Add(new[] { b[0] - gap, b[1] - gap, b[2] + gap, b[3] + gap });
                        double area = (b[2] - b[0]) * (b[3] - b[1]);
                        if (area > largest) { largest = area; reference = v; }
                    }
                }
                v = next;
            }

            var titleZone = TitleBlockZone(sheetW, sheetH);
            if (titleZone != null)
                occupied.Add(new[] { titleZone[0] - gap, titleZone[1] - gap, titleZone[2] + gap, titleZone[3] + gap });

            // A view that does not fit the paper at the requested scale is SHRUNK until it does: an
            // off-sheet or overlapping view is a defect, a smaller view is merely smaller.
            double requested = 1.0;
            try { requested = view.ScaleDecimal; } catch { }

            Func<double[], bool> fits = (b) =>
            {
                if (b[0] < margin || b[1] < margin || b[2] > sheetW - margin || b[3] > sheetH - margin)
                    return false;
                foreach (var o in occupied)
                    if (!(b[2] < o[0] || b[0] > o[2] || b[3] < o[1] || b[1] > o[3]))
                        return false;
                return true;
            };

            // The caller's own position is honoured whenever it is legal (inside the paper, clear of
            // every other view). Only a colliding or off-sheet view is moved.
            var current = view.GetOutline() as double[];
            bool keepCurrent = !searchEvenIfLegal && current != null && current.Length >= 4 && fits(current);
            ExecLog.Write($"layout '{view.Name}' scale={requested:0.###} occupied={occupied.Count} search={searchEvenIfLegal} " +
                $"current={(current == null || current.Length < 4 ? "none" : $"[{current[0] * 1000:0.#},{current[1] * 1000:0.#},{current[2] * 1000:0.#},{current[3] * 1000:0.#}]")} keep={keepCurrent}");
            if (keepCurrent) return;

            var ladder = new List<double>();
            for (int i = 0; i <= 8; i++) ladder.Add(requested * (1.0 - 0.1 * i));
            if (!ladder.Contains(requested)) ladder.Insert(0, requested);

            foreach (var scale in ladder)
            {
                view.ScaleDecimal = scale;
                var nb = view.GetOutline() as double[];
                if (nb == null || nb.Length < 4) continue;
                double w = nb[2] - nb[0], h = nb[3] - nb[1];
                if (w <= 0 || h <= 0) continue;

                var candidates = new List<double[]>();
                if (reference != null)
                {
                    var rb = reference.GetOutline() as double[];
                    if (rb != null && rb.Length >= 4)
                    {
                        double rcx = (rb[0] + rb[2]) / 2.0, rcy = (rb[1] + rb[3]) / 2.0;
                        double clear = gap + 0.003;   // the fit test touches at exactly gap: leave air
                        candidates.Add(new[] { rcx, rb[1] - clear - h / 2.0 });
                        candidates.Add(new[] { rcx, rb[3] + clear + h / 2.0 });
                        candidates.Add(new[] { rb[2] + clear + w / 2.0, rcy });
                        candidates.Add(new[] { rb[0] - clear - w / 2.0, rcy });
                    }
                }
                for (double cy = sheetH - margin - h / 2.0; cy >= margin + h / 2.0; cy -= 0.010)
                    for (double cx = margin + w / 2.0; cx <= sheetW - margin - w / 2.0; cx += 0.010)
                        candidates.Add(new[] { cx, cy });

                foreach (var c in candidates)
                {
                    var b = new[] { c[0] - w / 2.0, c[1] - h / 2.0, c[0] + w / 2.0, c[1] + h / 2.0 };
                    if (!fits(b)) continue;
                    view.Position = new[] { c[0], c[1], 0.0 };
                    var after = view.GetOutline() as double[];
                    ExecLog.Write($"layout '{view.Name}' placed at ({c[0] * 1000:0.#},{c[1] * 1000:0.#}) " +
                        $"scale={scale:0.###} box={(after == null || after.Length < 4 ? "none" : $"[{after[0] * 1000:0.#},{after[1] * 1000:0.#},{after[2] * 1000:0.#},{after[3] * 1000:0.#}]")}");
                    return;
                }
            }
        }

        // =====================================================================
        // set_drawing_sheet
        // =====================================================================
        private static double[] TitleBlockZone(double sheetW, double sheetH)
        {
            const double margin = 0.015;
            const double blockWidth = 0.180;
            const double blockHeight = 0.045;
            if (sheetW <= 2 * margin + 0.05 || sheetH <= 2 * margin + 0.03) return null;
            double w = Math.Min(blockWidth, sheetW - 2 * margin);
            double h = Math.Min(blockHeight, sheetH - 2 * margin);
            return new[] { sheetW - margin - w, margin, sheetW - margin, margin + h };
        }

        private void SpreadViews(IDrawingDoc drawingDoc, double verticalGap, double horizontalGap)
        {
            if (verticalGap <= 0 && horizontalGap <= 0) return;

            for (int pass = 0; pass < 8; pass++)
            {
                var views = new List<IView>();
                var vb = drawingDoc.GetFirstView() as IView;
                while (vb != null)
                {
                    IView next = null;
                    try { next = vb.GetNextView() as IView; } catch { }
                    if (vb.Type != 1) views.Add(vb);
                    vb = next;
                }

                bool moved = false;
                for (int i = 0; i < views.Count; i++)
                    for (int j = i + 1; j < views.Count; j++)
                    {
                        var a = views[i].GetOutline() as double[];
                        var b = views[j].GetOutline() as double[];
                        if (a == null || a.Length < 4 || b == null || b.Length < 4) continue;

                        bool xBand = a[0] < b[2] && a[2] > b[0];
                        bool yBand = a[1] < b[3] && a[3] > b[1];
                        if (!xBand || !yBand)
                        {
                            double sepX = Math.Max(b[0] - a[2], a[0] - b[2]);
                            double sepY = Math.Max(b[1] - a[3], a[1] - b[3]);
                            if (sepX >= horizontalGap || sepY >= verticalGap) continue;
                        }

                        double acx = (a[0] + a[2]) / 2.0, acy = (a[1] + a[3]) / 2.0;
                        double bcx = (b[0] + b[2]) / 2.0, bcy = (b[1] + b[3]) / 2.0;

                        double dy = bcy <= acy ? (a[1] - verticalGap) - b[3] : (a[3] + verticalGap) - b[1];
                        double dx = bcx >= acx ? (a[2] + horizontalGap) - b[0] : (a[0] - horizontalGap) - b[2];

                        // A push larger than the sheet itself is always a runaway: clamp it so the loop
                        // can never fling a view off the paper (measured: the section view landed at
                        // y = -11897 mm).
                        dx = Math.Max(-0.060, Math.Min(0.060, dx));
                        dy = Math.Max(-0.060, Math.Min(0.060, dy));

                        double mx = Math.Abs(dx) <= Math.Abs(dy) ? dx : 0.0;
                        double my = Math.Abs(dx) <= Math.Abs(dy) ? 0.0 : dy;

                        var pos = views[j].Position as double[];
                        if (pos == null || pos.Length < 2) continue;
                        views[j].Position = new[] { pos[0] + mx, pos[1] + my, 0.0 };
                        moved = true;
                    }
                if (!moved) return;
            }
        }

        private double[] FreeMargins(IDrawingDoc drawingDoc, IView self,
            double x1, double y1, double x2, double y2, double sheetW, double sheetH, double clearance)
        {
            double left = x1, right = sheetW - x2, bottom = y1, top = sheetH - y2;

            var boxes = new List<double[]>();
            string selfName = self.Name;
            var v = drawingDoc.GetFirstView() as IView;
            while (v != null)
            {
                IView next = null;
                try { next = v.GetNextView() as IView; } catch { }
                if (v.Type != 1 && v.Name != selfName)
                {
                    var b = v.GetOutline() as double[];
                    if (b != null && b.Length >= 4) boxes.Add(b);
                }
                v = next;
            }
            var zone = TitleBlockZone(sheetW, sheetH);
            if (zone != null) boxes.Add(zone);

            foreach (var b in boxes)
            {
                bool xBand = b[0] < x2 && b[2] > x1;
                bool yBand = b[1] < y2 && b[3] > y1;
                if (xBand && b[3] <= y1) bottom = Math.Min(bottom, y1 - b[3]);
                if (xBand && b[1] >= y2) top = Math.Min(top, b[1] - y2);
                if (yBand && b[2] <= x1) left = Math.Min(left, x1 - b[2]);
                if (yBand && b[0] >= x2) right = Math.Min(right, b[0] - x2);
            }

            return new[]
            {
                Math.Max(0.0, left - clearance),
                Math.Max(0.0, right - clearance),
                Math.Max(0.0, bottom - clearance),
                Math.Max(0.0, top - clearance)
            };
        }

        private void CenterViewGroup(IDrawingDoc drawingDoc, double spreadGap)
        {
            const double gap = 0.012;
            const double margin = 0.015;

            double sheetW = 0.420, sheetH = 0.297;
            var sheet = drawingDoc.GetCurrentSheet() as ISheet;
            if (sheet != null) sheet.GetSize(ref sheetW, ref sheetH);

            SpreadViews(drawingDoc, spreadGap, Math.Min(spreadGap, 0.038));

            var views = new List<IView>();
            var boxes = new List<double[]>();
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            var v = drawingDoc.GetFirstView() as IView;
            while (v != null)
            {
                IView next = null;
                try { next = v.GetNextView() as IView; } catch { }
                if (v.Type != 1)
                {
                    var b = v.GetOutline() as double[];
                    if (b != null && b.Length >= 4)
                    {
                        views.Add(v);
                        boxes.Add(b);
                        minX = Math.Min(minX, b[0]);
                        minY = Math.Min(minY, b[1]);
                        maxX = Math.Max(maxX, b[2]);
                        maxY = Math.Max(maxY, b[3]);
                    }
                }
                v = next;
            }
            if (views.Count == 0) return;

            var zone = TitleBlockZone(sheetW, sheetH);
            double dx = (margin + sheetW - margin) / 2.0 - (minX + maxX) / 2.0;
            double dy = (margin + sheetH - margin) / 2.0 - (minY + maxY) / 2.0;

            Func<double, double, bool> legal = (sx, sy) =>
            {
                foreach (var b in boxes)
                {
                    double x1 = b[0] + sx, y1 = b[1] + sy, x2 = b[2] + sx, y2 = b[3] + sy;
                    if (x1 < margin || y1 < margin || x2 > sheetW - margin || y2 > sheetH - margin)
                        return false;
                    if (zone != null && !(x2 < zone[0] - gap || x1 > zone[2] + gap ||
                                          y2 < zone[1] - gap || y1 > zone[3] + gap))
                        return false;
                }
                return true;
            };

            var shifts = new[]
            {
                dy, dy + 0.020, dy + 0.040, dy + 0.060, dy + 0.080,
                dy - 0.020, dy - 0.040, dy - 0.060
            };
            double chosen = double.NaN;
            foreach (var s in shifts)
                if (legal(dx, s)) { chosen = s; break; }

            if (double.IsNaN(chosen))
            {
                if (!legal(dx, dy)) return;
                chosen = dy;
            }

            foreach (var view in views)
            {
                var pos = view.Position as double[];
                if (pos == null || pos.Length < 2) continue;
                view.Position = new[] { pos[0] + dx, pos[1] + chosen, 0.0 };
            }

            ExecLog.Write($"layout centre dx={dx * 1000:0.#} dy={chosen * 1000:0.#} views={views.Count} " +
                $"zone={(zone == null ? "none" : $"[{zone[0] * 1000:0},{zone[1] * 1000:0},{zone[2] * 1000:0},{zone[3] * 1000:0}]")}");
        }

        private void ApplyLinearUnits(IModelDoc2 modelDoc, int unit, int unitSystem, int decimals)
        {
            var ext = modelDoc.Extension;
            ext.SetUserPreferenceInteger(SW_PREF_UNIT_SYSTEM, 0, unitSystem);
            ext.SetUserPreferenceInteger(SW_PREF_UNITS_LINEAR, 0, unit);
            ext.SetUserPreferenceInteger(SW_PREF_UNITS_DECIMALS, 0, decimals);
            ext.SetUserPreferenceDouble((int)swUserPreferenceDoubleValue_e.swDetailingDimFontHeight, 0, 0.0028);
            ext.SetUserPreferenceDouble((int)swUserPreferenceDoubleValue_e.swDetailingNoteFontHeight, 0, 0.0030);
            modelDoc.GraphicsRedraw2();
        }
        public ExecutionResponse SetDrawingSheet(ToolRequest request)
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
                var drawingDoc = _solidWorks.IActiveDoc2 as IDrawingDoc;
                if (drawingDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "set_drawing_sheet requires an active drawing document.");

                string sheetName = p?.Value<string>("sheet") ?? "";
                string size = p?.Value<string>("size") ?? "";
                string orientation = p?.Value<string>("orientation") ?? "landscape";
                double customW = p?.Value<double?>("width") ?? 0.0;
                double customH = p?.Value<double?>("height") ?? 0.0;

                if (!string.IsNullOrEmpty(sheetName) && !drawingDoc.ActivateSheet(sheetName))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SHEET_NOT_FOUND", "No sheet named '" + sheetName + "' in the active drawing.");

                var sheet = drawingDoc.GetCurrentSheet() as ISheet;
                if (sheet == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_SHEET", "The active drawing has no current sheet.");

                if (!string.IsNullOrEmpty(size))
                {
                    int paper; double w, h;
                    if (!MapPaperSize(size, orientation, customW, customH, out paper, out w, out h))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "UNSUPPORTED_SIZE", "size must be one of: A4, A3, A2, A1, A0, custom.");
                    if (!ApplySheetSize(sheet, paper, w, h, size))
                    {
                        double gotW = 0, gotH = 0;
                        sheet.GetSize(ref gotW, ref gotH);
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SHEET_SIZE_FAILED",
                            "SolidWorks kept the sheet at " + R6(gotW) + " x " + R6(gotH) +
                            " m instead of the requested " + R6(w) + " x " + R6(h) + " m.");
                    }
                }

                // NOTE - the sheet SCALE is deliberately not set here. On a sheet that carries a
                // sheet format (Sheet1 of any drawing made from the template) ISheet.SetScale does
                // not produce the ratio you asked for: SetScale(1,2) reads back as "1:24", while
                // SetScale(1,1) reads back as "1:1" - the format's own scale is folded in. On a
                // format-less sheet (add_drawing_sheet) it behaves exactly. Since the drawing scale
                // that matters is the VIEW scale (add_drawing_view), and the printed scale is free
                // text on the sheet, this tool leaves the sheet scale alone.
                var modelDoc = (IModelDoc2)drawingDoc;
                modelDoc.GraphicsRedraw2();

                double aw = 0, ah = 0;
                sheet.GetSize(ref aw, ref ah);

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
                        DocumentType = "DRAWING",
                        ActiveSketch = null,
                        Features = new List<string> { sheet.GetName() },
                        Dimensions = new List<string>()
                    },
                    ResultGeometry = new JObject
                    {
                        ["sheet"] = sheet.GetName(),
                        ["size"] = size,
                        ["orientation"] = orientation,
                        ["width"] = R6(aw),
                        ["height"] = R6(ah),
                        ["sheet_count"] = drawingDoc.GetSheetCount()
                    },
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.Message);
            }
        }

        // =====================================================================
        // add_drawing_sheet
        // =====================================================================
        public ExecutionResponse AddDrawingSheet(ToolRequest request)
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
                var drawingDoc = _solidWorks.IActiveDoc2 as IDrawingDoc;
                if (drawingDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "add_drawing_sheet requires an active drawing document.");

                string name = p?.Value<string>("name") ?? "";
                string size = p?.Value<string>("size") ?? "A4";
                string orientation = p?.Value<string>("orientation") ?? "landscape";
                double customW = p?.Value<double?>("width") ?? 0.0;
                double customH = p?.Value<double?>("height") ?? 0.0;
                bool activate = p?.Value<bool?>("activate") ?? true;

                int paper; double w, h;
                if (!MapPaperSize(size, orientation, customW, customH, out paper, out w, out h))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "UNSUPPORTED_SIZE", "size must be one of: A4, A3, A2, A1, A0, custom.");

                if (string.IsNullOrEmpty(name))
                    name = "Sheet" + (drawingDoc.GetSheetCount() + 1);

                // swDwgTemplateNone: add a sheet with NO sheet format (no border/title block),
                // matching the plain-text layout this tool set produces. The paper size still
                // comes from the paper enum + width/height. Scale 1:1 - a new sheet has no format
                // to fold in, and the drawing scale that matters is the view scale.
                // NewSheet2's return value is NOT reliable: after an ISheet.SetSize on the current
                // sheet it returns FALSE while still creating the sheet (verified). Trust the sheet
                // COUNT instead, and take the new sheet's NAME from the sheet list, because
                // SolidWorks renames on a collision rather than failing.
                var sheetBefore = drawingDoc.GetCurrentSheet() as ISheet;
                string previousName = sheetBefore != null ? sheetBefore.GetName() : "";

                int sheetsBefore = drawingDoc.GetSheetCount();
                drawingDoc.NewSheet2(name, paper, SW_TEMPLATE_NONE, 1.0, 1.0, false, "", w, h);
                int sheetsAfter = drawingDoc.GetSheetCount();
                string newName = NewSheetName(drawingDoc, sheetsBefore, name);
                if (sheetsAfter <= sheetsBefore)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SHEET_CREATE_FAILED",
                        "SolidWorks kept the drawing at " + sheetsBefore + " sheet(s).");

                // The paper size passed to NewSheet2 is ignored just as often: the new sheet can
                // inherit the CURRENT sheet's size (verified - a landscape request on a portrait
                // sheet came back portrait). Make it current, set the size the same verified way
                // set_drawing_sheet does, and read it back.
                drawingDoc.ActivateSheet(newName);
                var sheet = drawingDoc.GetCurrentSheet() as ISheet;
                double aw = w, ah = h;
                if (sheet != null)
                {
                    if (!ApplySheetSize(sheet, paper, w, h, size))
                    {
                        double gw = 0, gh = 0;
                        sheet.GetSize(ref gw, ref gh);
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SHEET_SIZE_FAILED",
                            "The new sheet stayed at " + R6(gw) + " x " + R6(gh) +
                            " m instead of the requested " + R6(w) + " x " + R6(h) + " m.");
                    }
                    sheet.GetSize(ref aw, ref ah);
                }

                if (!activate && !string.IsNullOrEmpty(previousName))
                    drawingDoc.ActivateSheet(previousName);

                var modelDoc = (IModelDoc2)drawingDoc;
                modelDoc.GraphicsRedraw2();

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
                        DocumentType = "DRAWING",
                        ActiveSketch = null,
                        Features = new List<string> { newName },
                        Dimensions = new List<string>()
                    },
                    ResultGeometry = new JObject
                    {
                        ["sheet"] = newName,
                        ["size"] = size,
                        ["orientation"] = orientation,
                        ["width"] = R6(aw),
                        ["height"] = R6(ah),
                        ["sheet_count"] = drawingDoc.GetSheetCount()
                    },
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.Message);
            }
        }

        // =====================================================================
        // add_drawing_note
        // =====================================================================
        public ExecutionResponse AddDrawingNote(ToolRequest request)
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
                string text = p?.Value<string>("text");
                if (text == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "text is required.");
                double? xIn = p?.Value<double?>("x");
                double? yIn = p?.Value<double?>("y");
                if (xIn == null || yIn == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "x and y are required (note position on the sheet, meters).");

                double height = p?.Value<double?>("height") ?? 0.0035;
                string justification = p?.Value<string>("justification") ?? "left";
                bool bold = p?.Value<bool?>("bold") ?? false;
                bool italic = p?.Value<bool?>("italic") ?? false;
                bool leader = p?.Value<bool?>("leader") ?? false;
                double leaderX = p?.Value<double?>("leader_x") ?? 0.0;
                double leaderY = p?.Value<double?>("leader_y") ?? 0.0;
                string leaderType = p?.Value<string>("leader_type") ?? "edge";
                string leaderSide = p?.Value<string>("leader_side") ?? "right";
                string sheetName = p?.Value<string>("sheet") ?? "";

                var drawingDoc = _solidWorks.IActiveDoc2 as IDrawingDoc;
                if (drawingDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "add_drawing_note requires an active drawing document.");

                if (!string.IsNullOrEmpty(sheetName) && !drawingDoc.ActivateSheet(sheetName))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SHEET_NOT_FOUND", "No sheet named '" + sheetName + "' in the active drawing.");

                var modelDoc = (IModelDoc2)drawingDoc;
                string resolved = ResolveNotePlaceholders(text, drawingDoc, modelDoc);
                int before = TotalNoteCount(drawingDoc);

                modelDoc.ClearSelection2(true);

                Note note;
                if (leader)
                {
                    string selectType = SelectTypeFor(leaderType);
                    if (selectType == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "UNSUPPORTED_LEADER_TYPE",
                            "leader_type must be one of: edge, vertex, face, silhouette, sketch_segment, component.");

                    if (!modelDoc.Extension.SelectByID2("", selectType, leaderX, leaderY, 0, false, 0, null, 0))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "LEADER_SELECT_FAILED",
                            "Nothing of type '" + leaderType + "' at the leader point on the sheet; check leader_x/leader_y.");

                    drawingDoc.InsertNewNote(resolved, false, false, true, SW_ARROW_OPEN, (short)MapLeaderSide(leaderSide));
                    note = ((ISelectionMgr)modelDoc.SelectionManager).GetSelectedObject6(1, -1) as Note;
                    // InsertNewNote usually leaves the note selected, but not always; fall back to
                    // finding it by its (resolved) text.
                    if (note == null)
                        note = FindNoteByText(drawingDoc, resolved);
                }
                else
                {
                    note = modelDoc.InsertNote(resolved) as Note;
                }

                if (note == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NOTE_CREATE_FAILED", "SolidWorks did not return the inserted note.");

                // Position + style. SetTextPoint moves the note's text anchor; a leader stays attached.
                note.SetTextPoint(xIn.Value, yIn.Value, 0);

                try { note.SetHeight(height); }
                catch (Exception) { /* height is best-effort: a locked style must not fail the note */ }

                note.SetTextJustification(MapJustification(justification));

                if (bold || italic)
                {
                    // Cosmetic, best-effort: SolidWorks exposes no separate "text format type" enum
                    // in this version, so 0 = the whole-note format.
                    try
                    {
                        var tf = note.GetTextFormat() as ITextFormat;
                        if (tf != null)
                        {
                            if (bold) tf.Bold = true;
                            if (italic) tf.Italic = true;
                            note.SetTextFormat(0, tf);
                        }
                    }
                    catch (Exception) { /* font styling is cosmetic */ }
                }

                modelDoc.GraphicsRedraw2();

                var sheet = drawingDoc.GetCurrentSheet() as ISheet;
                int after = TotalNoteCount(drawingDoc);

                string noteName = "";
                try
                {
                    var ann = note.GetAnnotation() as IAnnotation;
                    if (ann != null) noteName = ann.GetName();
                }
                catch (Exception) { }

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
                        DocumentType = "DRAWING",
                        ActiveSketch = null,
                        Features = new List<string> { string.IsNullOrEmpty(noteName) ? "Note" : noteName },
                        Dimensions = new List<string>()
                    },
                    ResultGeometry = new JObject
                    {
                        ["text"] = text,
                        ["resolved_text"] = resolved,
                        ["note"] = noteName,
                        // Only the DELTA is reported, not the absolute count: SolidWorks' view
                        // traversal over-counts once a drawing has more than one sheet (1 note
                        // reads back as 58), so an absolute number would be a lie. The delta is
                        // measured against itself and stays honest.
                        ["added"] = after - before,
                        ["sheet"] = sheet != null ? sheet.GetName() : sheetName,
                        ["x"] = R6(xIn.Value),
                        ["y"] = R6(yIn.Value),
                        ["height"] = R6(height),
                        ["leader"] = leader
                    },
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.Message);
            }
        }

        // =====================================================================
        // add_balloon
        // =====================================================================
        public ExecutionResponse AddBalloon(ToolRequest request)
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
                double? xIn = p?.Value<double?>("x");
                double? yIn = p?.Value<double?>("y");
                if (xIn == null || yIn == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "x and y are required (a point on the component, sheet meters).");

                string text = p?.Value<string>("text") ?? "";
                string style = p?.Value<string>("style") ?? "circle";
                string size = p?.Value<string>("size") ?? "auto";

                int styleValue;
                if (!MapBalloonStyle(style, out styleValue))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "UNSUPPORTED_STYLE",
                        "style must be one of: circle, box, square, hexagon, diamond, triangle, underline.");

                var drawingDoc = _solidWorks.IActiveDoc2 as IDrawingDoc;
                if (drawingDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "add_balloon requires an active drawing document.");

                var modelDoc = (IModelDoc2)drawingDoc;
                modelDoc.ClearSelection2(true);

                if (!modelDoc.Extension.SelectByID2("", "COMPONENT", xIn.Value, yIn.Value, 0, false, 0, null, 0))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "COMPONENT_SELECT_FAILED",
                        "No component under the point; check x/y (sheet meters) and that the view shows a component there.");

                var ext = modelDoc.Extension;
                BalloonOptions opts = ext.CreateBalloonOptions();
                opts.Style = styleValue;
                ApplyBalloonSize(opts, size);
                if (string.IsNullOrEmpty(text))
                    opts.UpperTextContent = SW_BALLOON_TEXT_ITEM_NUMBER;
                else
                {
                    opts.UpperTextContent = SW_BALLOON_TEXT_CUSTOM;
                    opts.UpperText = text;
                }

                Note note = ext.InsertBOMBalloon2(opts);
                if (note == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "BALLOON_CREATE_FAILED", "SolidWorks did not create the balloon.");

                // IBalloonOptions.UpperText does not reliably survive InsertBOMBalloon2 (the balloon
                // comes back as "?"), so for custom text set it on the note itself - INote.SetText is
                // authoritative and is what the balloon renders.
                if (!string.IsNullOrEmpty(text))
                {
                    try { note.SetText(text); }
                    catch (Exception) { }
                }

                // Optional explicit balloon placement: the leader still attaches to the picked
                // component, but the balloon circle is moved to (text_x, text_y).
                double? textX = p?.Value<double?>("text_x");
                double? textY = p?.Value<double?>("text_y");
                if (textX != null && textY != null)
                {
                    try { note.SetTextPoint(textX.Value, textY.Value, 0); }
                    catch (Exception) { }
                }

                modelDoc.GraphicsRedraw2();

                string noteName = "";
                double annX = 0, annY = 0;
                try
                {
                    var ann = note.GetAnnotation() as IAnnotation;
                    if (ann != null)
                    {
                        noteName = ann.GetName();
                        var pos = ann.GetPosition() as double[];
                        if (pos != null && pos.Length >= 2) { annX = pos[0]; annY = pos[1]; }
                    }
                }
                catch (Exception) { }

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
                        DocumentType = "DRAWING",
                        ActiveSketch = null,
                        Features = new List<string> { string.IsNullOrEmpty(noteName) ? "Balloon" : noteName },
                        Dimensions = new List<string>()
                    },
                    ResultGeometry = new JObject
                    {
                        ["note"] = noteName,
                        ["text"] = note.GetText(),
                        ["style"] = style,
                        ["x"] = R6(xIn.Value),
                        ["y"] = R6(yIn.Value),
                        ["balloon_x"] = R6(annX),
                        ["balloon_y"] = R6(annY)
                    },
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.Message);
            }
        }

        // =====================================================================
        // auto_balloon
        // =====================================================================
        public ExecutionResponse AutoBalloon(ToolRequest request)
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
                string viewName = p?.Value<string>("view");
                if (string.IsNullOrEmpty(viewName))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "view is required (a drawing view name).");

                string layout = p?.Value<string>("layout") ?? "circle";
                string style = p?.Value<string>("style") ?? "circle";
                bool ignoreMultiple = p?.Value<bool?>("ignore_multiple") ?? true;
                int itemNumberStart = p?.Value<int?>("item_number_start") ?? 1;

                int layoutValue;
                if (!MapBalloonLayout(layout, out layoutValue))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "UNSUPPORTED_LAYOUT", "layout must be one of: square, circle, top, bottom, right, left.");
                int styleValue;
                if (!MapBalloonStyle(style, out styleValue))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "UNSUPPORTED_STYLE",
                        "style must be one of: circle, box, square, hexagon, diamond, triangle, underline.");

                var drawingDoc = _solidWorks.IActiveDoc2 as IDrawingDoc;
                if (drawingDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "auto_balloon requires an active drawing document.");

                var view = FindViewObjectByName(drawingDoc, viewName);
                if (view == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "VIEW_NOT_FOUND", "No drawing view named '" + viewName + "' on the active sheet.");

                var modelDoc = (IModelDoc2)drawingDoc;
                modelDoc.ClearSelection2(true);

                if (!modelDoc.Extension.SelectByID2(viewName, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "VIEW_SELECT_FAILED", "Could not select drawing view '" + viewName + "'.");

                int before = view.GetNoteCount();

                AutoBalloonOptions opts = drawingDoc.CreateAutoBalloonOptions();
                opts.Layout = layoutValue;
                opts.Style = styleValue;
                opts.IgnoreMultiple = ignoreMultiple;
                opts.ItemNumberStart = itemNumberStart;

                drawingDoc.AutoBalloon5(opts);

                modelDoc.GraphicsRedraw2();

                int after = view.GetNoteCount();
                var notes = new JArray();
                try
                {
                    var arr = view.GetNotes() as object[];
                    if (arr != null)
                    {
                        foreach (var o in arr)
                        {
                            var n = o as Note;
                            if (n != null) notes.Add(n.GetText());
                        }
                    }
                }
                catch (Exception) { }

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
                        DocumentType = "DRAWING",
                        ActiveSketch = null,
                        Features = new List<string> { viewName },
                        Dimensions = new List<string>()
                    },
                    ResultGeometry = new JObject
                    {
                        ["view"] = viewName,
                        ["count"] = after - before,
                        ["note_count"] = after,
                        ["layout"] = layout,
                        ["style"] = style,
                        ["notes"] = notes
                    },
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.Message);
            }
        }

        // =====================================================================
        // shared helpers
        // =====================================================================

        // Maps a paper name + orientation to (swDwgPaperSizes_e, width, height) in METERS.
        // A4 portrait has a dedicated enum; every other portrait orientation goes through
        // swDwgPapersUserDefined with an explicit width/height. "custom" uses the given
        // width/height verbatim (orientation is then ignored).
        private static bool MapPaperSize(string size, string orientation, double customW, double customH,
                                         out int paper, out double width, out double height)
        {
            string s = (size ?? "").Trim().ToLowerInvariant();
            if (s == "custom")
            {
                paper = SW_PAPER_USER; width = customW; height = customH; return true;
            }

            bool portrait = string.Equals((orientation ?? "").Trim(), "portrait", StringComparison.OrdinalIgnoreCase);
            double w, h;
            int std;
            switch (s)
            {
                case "a4": w = 0.297; h = 0.210; std = SW_PAPER_A4; break;
                case "a3": w = 0.420; h = 0.297; std = SW_PAPER_A3; break;
                case "a2": w = 0.594; h = 0.420; std = SW_PAPER_A2; break;
                case "a1": w = 0.841; h = 0.594; std = SW_PAPER_A1; break;
                case "a0": w = 1.189; h = 0.841; std = SW_PAPER_A0; break;
                default: paper = 0; width = 0; height = 0; return false;
            }

            if (portrait && s == "a4") { paper = SW_PAPER_A4_VERTICAL; width = 0.210; height = 0.297; return true; }
            if (portrait) { paper = SW_PAPER_USER; width = h; height = w; return true; }
            paper = std; width = w; height = h; return true;
        }

        private static bool MapLinearUnit(string name, out int unit, out int unitSystem)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "mm": case "millimeter": case "millimeters": unit = 0; unitSystem = SW_UNIT_SYSTEM_MMGS; return true;
                case "cm": case "centimeter": case "centimeters": unit = 1; unitSystem = SW_UNIT_SYSTEM_MMGS; return true;
                case "m": case "meter": case "meters": unit = 2; unitSystem = SW_UNIT_SYSTEM_MKS; return true;
                case "in": case "inch": case "inches": unit = 3; unitSystem = SW_UNIT_SYSTEM_IPS; return true;
                case "ft": case "foot": case "feet": unit = 4; unitSystem = SW_UNIT_SYSTEM_IPS; return true;
                case "ft-in": case "ftin": case "feet-inches": unit = 5; unitSystem = SW_UNIT_SYSTEM_IPS; return true;
                case "micron": case "microns": case "um": unit = 8; unitSystem = SW_UNIT_SYSTEM_CUSTOM; return true;
                case "mil": case "mils": unit = 9; unitSystem = SW_UNIT_SYSTEM_CUSTOM; return true;
                case "uin": case "microinch": unit = 10; unitSystem = SW_UNIT_SYSTEM_CUSTOM; return true;
                default: unit = 0; unitSystem = SW_UNIT_SYSTEM_MMGS; return false;
            }
        }

        private static string UnitName(int unit)
        {
            switch (unit)
            {
                case 0: return "mm";
                case 1: return "cm";
                case 2: return "m";
                case 3: return "in";
                case 4: return "ft";
                case 5: return "ft-in";
                case 6: return "angstrom";
                case 7: return "nm";
                case 8: return "micron";
                case 9: return "mil";
                case 10: return "uin";
                default: return "unit" + unit;
            }
        }

        private static string DocumentKindName(IModelDoc2 modelDoc)
        {
            switch (modelDoc.GetType())
            {
                case 1: return "PART";
                case 2: return "ASSEMBLY";
                case 3: return "DRAWING";
                default: return "UNKNOWN";
            }
        }

        private static string SelectTypeFor(string leaderType)
        {
            switch ((leaderType ?? "").Trim().ToLowerInvariant())
            {
                case "edge": return "EDGE";
                case "vertex": return "VERTEX";
                case "face": return "FACE";
                case "silhouette": return "SILHOUETTE";
                case "sketch_segment": return "SKETCHSEGMENT";
                case "component": return "COMPONENT";
                default: return null;
            }
        }

        private static int MapLeaderSide(string side)
        {
            switch ((side ?? "").Trim().ToLowerInvariant())
            {
                case "left": return SW_LEADER_LEFT;
                case "smart": return SW_LEADER_SMART;
                default: return SW_LEADER_RIGHT;
            }
        }

        private static int MapJustification(string justification)
        {
            switch ((justification ?? "").Trim().ToLowerInvariant())
            {
                case "center": case "centre": return SW_JUSTIFY_CENTER;
                case "right": return SW_JUSTIFY_RIGHT;
                default: return SW_JUSTIFY_LEFT;
            }
        }

        private static bool MapBalloonStyle(string style, out int value)
        {
            switch ((style ?? "").Trim().ToLowerInvariant())
            {
                case "circle": value = 1; return true;
                case "triangle": value = 2; return true;
                case "hexagon": value = 3; return true;
                case "box": value = 4; return true;
                case "diamond": value = 5; return true;
                case "underline": value = 10; return true;
                case "square": value = 11; return true;
                default: value = 0; return false;
            }
        }

        private static bool MapBalloonLayout(string layout, out int value)
        {
            switch ((layout ?? "").Trim().ToLowerInvariant())
            {
                case "square": value = 1; return true;
                case "circle": value = 2; return true;
                case "top": value = 3; return true;
                case "bottom": value = 4; return true;
                case "right": value = 5; return true;
                case "left": value = 6; return true;
                default: value = 0; return false;
            }
        }

        // Balloon "size" is swBalloonFit_e (how many characters the balloon must fit).
        // "auto"/empty leaves the SolidWorks default untouched.
        private static void ApplyBalloonSize(BalloonOptions opts, string size)
        {
            switch ((size ?? "").Trim().ToLowerInvariant())
            {
                case "1": opts.Size = 1; break;
                case "2": opts.Size = 2; break;
                case "3": opts.Size = 3; break;
                case "4": opts.Size = 4; break;
                case "5": opts.Size = 5; break;
                default: break; // "auto": keep the document default
            }
        }

        private static int TotalNoteCount(IDrawingDoc drawingDoc)
        {
            int total = 0;
            var view = drawingDoc.GetFirstView() as IView;
            while (view != null)
            {
                total += view.GetNoteCount();
                view = view.GetNextView() as IView;
            }
            return total;
        }

        // Sets the sheet size and VERIFIES it by reading the sheet back: ISheet.SetSize's return
        // value is not reliable for every standard paper enum (verified: swDwgPaperA4size returns
        // false even though the size was applied). If the standard enum does not take, retry as a
        // user-defined size before giving up.
        private static bool ApplySheetSize(ISheet sheet, int paper, double w, double h, string size)
        {
            sheet.SetSize(paper, w, h);
            if (SheetMatches(sheet, w, h))
                return true;

            if (!string.Equals((size ?? "").Trim(), "custom", StringComparison.OrdinalIgnoreCase))
            {
                sheet.SetSize(SW_PAPER_USER, w, h);
                if (SheetMatches(sheet, w, h))
                    return true;
            }
            return false;
        }

        private static bool SheetMatches(ISheet sheet, double w, double h)
        {
            double aw = 0, ah = 0;
            sheet.GetSize(ref aw, ref ah);
            double tol = 5e-4; // 0.5 mm - sheet paper sizes are exact
            return Math.Abs(aw - w) < tol && Math.Abs(ah - h) < tol;
        }

        // The name of the sheet just added: the LAST name in the current list once it has grown.
        // SolidWorks renames a new sheet instead of refusing a duplicate name, so the requested
        // name is only a fallback.
        private static string NewSheetName(IDrawingDoc drawingDoc, int before, string requested)
        {
            try
            {
                var names = drawingDoc.GetSheetNames() as string[];
                if (names != null && names.Length > before)
                    return names[names.Length - 1];
            }
            catch (Exception) { }
            return requested;
        }

        // The scale worth printing is the scale the model is DRAWN at - the first model view - not
        // the sheet's own ratio. On a template sheet the sheet's ScaleRatio folds in the sheet
        // format's scale (ask for 1:2, read back 1:24), so it is not a number anyone asked for.
        private static IView FirstModelView(IDrawingDoc drawingDoc)
        {
            var view = drawingDoc.GetFirstView() as IView;
            while (view != null)
            {
                if (view.Type != 1) // 1 = swDrawingSheet: the sheet's own view, not a model view
                    return view;
                view = view.GetNextView() as IView;
            }
            return null;
        }

        // Finds a note on any sheet view by its exact text (used when InsertNewNote does not leave
        // the new note selected).
        private static Note FindNoteByText(IDrawingDoc drawingDoc, string text)
        {
            Note found = null;
            var view = drawingDoc.GetFirstView() as IView;
            while (view != null)
            {
                var notes = view.GetNotes() as object[];
                if (notes != null)
                {
                    foreach (var o in notes)
                    {
                        var n = o as Note;
                        if (n != null && string.Equals(n.GetText(), text, StringComparison.Ordinal))
                            found = n;
                    }
                }
                view = view.GetNextView() as IView;
            }
            return found;
        }

        private static int CurrentPageNumber(IDrawingDoc drawingDoc, string sheetName)
        {
            var names = drawingDoc.GetSheetNames() as string[];
            if (names != null)
            {
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], sheetName, StringComparison.OrdinalIgnoreCase))
                        return i + 1;
                }
            }
            return 1;
        }

        private static string ScaleText(IView view)
        {
            try
            {
                var ratio = view != null ? view.ScaleRatio as double[] : null;
                if (ratio != null && ratio.Length >= 2 && Math.Abs(ratio[1]) > 1e-9)
                    return Num(ratio[0]) + ":" + Num(ratio[1]);
            }
            catch (Exception) { }
            return "";
        }

        private static string Num(double v)
        {
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        // Resolves {placeholders} in note text from the drawing and its referenced model.
        // Unknown placeholders are left untouched so a literal brace in the text survives.
        private string ResolveNotePlaceholders(string text, IDrawingDoc drawingDoc, IModelDoc2 modelDoc)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0)
                return text;

            var sheet = drawingDoc.GetCurrentSheet() as ISheet;
            string sheetName = sheet != null ? sheet.GetName() : "";

            string partName = "";
            string material = "";
            var view = drawingDoc.GetFirstView() as IView;
            while (view != null)
            {
                string refName = view.GetReferencedModelName();
                if (!string.IsNullOrEmpty(refName))
                {
                    partName = System.IO.Path.GetFileNameWithoutExtension(refName);
                    try
                    {
                        var refDoc = view.ReferencedDocument as IModelDoc2;
                        var part = refDoc as IPartDoc;
                        if (part != null)
                        {
                            string db = "";
                            material = part.GetMaterialPropertyName2("", out db) ?? "";
                        }
                    }
                    catch (Exception) { }
                    break;
                }
                view = view.GetNextView() as IView;
            }

            string units = UnitName(modelDoc.Extension.GetUserPreferenceInteger(SW_PREF_UNITS_LINEAR, 0));

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["part_name"] = partName,
                ["material"] = material,
                ["model"] = partName,
                ["drawing"] = System.IO.Path.GetFileNameWithoutExtension(modelDoc.GetPathName()),
                ["sheet"] = sheetName,
                ["page"] = CurrentPageNumber(drawingDoc, sheetName).ToString(CultureInfo.InvariantCulture),
                ["pages"] = drawingDoc.GetSheetCount().ToString(CultureInfo.InvariantCulture),
                ["scale"] = ScaleText(FirstModelView(drawingDoc)),
                ["date"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["units"] = units
            };

            string result = text;
            foreach (var kv in map)
                result = result.Replace("{" + kv.Key + "}", kv.Value ?? "");
            return result;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // SolidWorksService partial: auto_dimension_drawing mode="essentials".
    //
    // WHY THIS EXISTS (the measured failure of the model-item route)
    // ---------------------------------------------------------------
    // AutoDimensionDrawing ("model" mode) delegates to IDrawingDoc.InsertModelAnnotations3, which
    // imports EVERY model dimension flagged for the drawing. Measured on the Auto Grid Cutter parts
    // that is 21-60 dimensions per view: they land where SolidWorks wants them, they collide, and the
    // sheet stops being readable. The 19 client reference sheets show the opposite convention: a
    // CURATED set (typically 6-16 dimensions) consisting of
    //     * the overall envelope (overall width + overall height),
    //     * hole callouts, one per DISTINCT diameter ("4xØ21", "Ø28 THRU"),
    //     * hole POSITIONS measured from a baseline edge, one row of chained X dimensions per row of
    //       holes plus one Y dimension per row,
    //     * a few radii/angles/thicknesses.
    // "essentials" is therefore written from scratch: it inserts NOTHING from the model, reads the
    // projected geometry of each view, and creates exactly the dimensions above at positions it
    // computes itself, so they cannot land on top of each other.
    //
    // COORDINATE MODEL (A/B verified against IView.GetOutline on a live sheet)
    // ----------------------------------------------------------------------
    //   sheet_point = view.Position + view.ScaleDecimal * view_plane_point
    // The drawing sheet origin (0,0) is the paper's LOWER-LEFT corner (an A4 landscape sheet container
    // reports GetOutline = [0, 0, 0.297, 0.21]). Everything internal is metres; the JSON echoes mm.
    // Only ORTHOGRAPHIC/section views are dimensioned (both frame axes axis-aligned); the shaded
    // isometric - which the reference sheets never dimension - is skipped automatically.
    //
    // PICK RELIABILITY (the four things a coordinate pick gets wrong, all measured)
    // ---------------------------------------------------------------------------
    // 1. A pick computed exactly on an edge can miss (a silhouette computed at sheet x=97.5 mm was
    //    first hit from x=97.8 mm), so each candidate point is micro-probed over a small radius.
    // 2. A pick on a CORNER returns whichever edge SolidWorks snaps to first — at a view's extreme
    //    corner it returns the PERPENDICULAR edge, which then produces an ANGLE dimension instead of
    //    a distance (measured: expected 406.00 mm, produced 1570.80 mm = pi/2 radians = 90°).
    //    Hence picks are made at 50/40/60/30/70...% ALONG a line and at several angles AROUND a
    //    circle, never at an endpoint, and the entity is VERIFIED (vertical line / horizontal line /
    //    circle of a given radius) with a rejected probe rolled back and logged.
    // 3. Two circles dimension as their 2D CENTRE-TO-CENTRE distance, not the axis projection
    //    (measured: expected 45.00 mm on same-row holes, produced 54.08 mm = sqrt(45²+30²) once the
    //    holes were in different rows). Hence holes are grouped into ROWS and chained within a row,
    //    where the aligned distance IS the X distance, and each row is tied to the baseline edge.
    // 4. A dimension whose created value disagrees with the geometry is DELETED, never printed.
    // Every attempt/rejection goes to execution.log; the caller also gets created[]/skipped[] with
    // value_mm vs expected_mm and match.
    public partial class SolidWorksService
    {
        // An entity to pick, as an ordered list of candidate SHEET points (metres).
        private sealed class EntityPick
        {
            public string Expect;          // "vline" | "hline" | "circle" | "any"
            public double ExpectR;         // circle radius in metres, 0 = unchecked
            public double[][] Points;
            public double[][] VertexPoints;   // corner fallback (see PickAt)
            public double[] XDir, YDir;       // the owning view's frame axes (view-plane classification)
        }

        private sealed class PickInfo
        {
            public string Text = "none";
            public bool IsVertical, IsHorizontal, IsCircle, IsVertex, IsSketchPoint;
            public double R;
        }

        // ---------------------------------------------------------------- public entry (mode dispatch)
        internal ExecutionResponse AutoDimensionEssentials(ToolRequest request, JObject p)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
            if (modelDoc == null)
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

            var drawingDoc = modelDoc as IDrawingDoc;
            if (drawingDoc == null)
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "NOT_A_DRAWING", "Active document is not a drawing. Use create_drawing + add_drawing_view first.");

            double gap = p?.Value<double?>("gap") ?? 0.012;                 // metres between dimension rows
            bool callouts = p?.Value<bool?>("include_callouts") ?? true;
            bool holePos = p?.Value<bool?>("include_hole_positions") ?? true;
            bool circleDiams = p?.Value<bool?>("include_circle_diameters") ?? true;
            bool clearExisting = p?.Value<bool?>("clear_existing") ?? false;
            bool centerGroup = p?.Value<bool?>("center_group") ?? true;
            double viewGap = p?.Value<double?>("view_gap") ?? 0.050;

            var created = new JArray();
            var skipped = new JArray();
            int total = 0;
            // One measurement is dimensioned ONCE per sheet: the same envelope appears in every
            // orthographic view, and the reference sheets never print a value twice (the measured
            // failure: 406.00 on the front AND the top view, 151.00 twice, 14.00 twice).
            var seen = new Dictionary<long, string>();

            bool prevInputDimVal = _solidWorks.GetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            _solidWorks.SetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);
            try
            {
                try { modelDoc.ClearSelection2(true); } catch { }

                // The sheet container reports [0,0,paperW,paperH] — the free-room reference for placing
                // dimension text INSIDE the paper (an off-sheet dimension is not printed at all).
                double[] sheetRect = null;
                try
                {
                    var sheetView = drawingDoc.GetFirstView() as IView;
                    if (sheetView != null) sheetRect = sheetView.GetOutline() as double[];
                }
                catch { }
                if (sheetRect == null || sheetRect.Length < 4)
                    sheetRect = new double[] { 0.0, 0.0, 0.297, 0.210 };

                // Re-run safety: a curated sheet is OWNED by this mode, so refreshing it must replace the
                // previous dimensions/hole callouts instead of piling a second copy on top. Without this
                // every re-run forces a brand-new drawing document (the measured 14-document mess).
                if (clearExisting) RemoveSheetDisplayDimensions(drawingDoc, modelDoc, skipped);

                // The whole view group is centred on the paper, with the title-block corner kept
                // free, BEFORE any dimension is placed: annotations are then laid out against the
                // final view positions instead of positions that move afterwards.
                if (centerGroup) CenterViewGroup(drawingDoc, viewGap);

                object viewObj = drawingDoc.GetFirstView();
                int guard = 0;
                var ortho = new List<IView>();
                var geomOf = new Dictionary<string, ViewGeomData>();
                while (viewObj != null && guard++ < 500)
                {
                    var view = viewObj as IView;
                    object next = null;
                    if (view != null)
                    {
                        try { next = view.GetNextView(); } catch { }
                        int vType = -1; try { vType = view.Type; } catch { }
                        if (vType != 1)   // 1 = the sheet container view
                        {
                            double[] fo, fx, fy;
                            bool aligned = false;
                            try
                            {
                                aligned = ViewModelFrameRaw(view, out fo, out fx, out fy)
                                    && PrincipalAxis(fx[0], fx[1], fx[2]) != null
                                    && PrincipalAxis(fy[0], fy[1], fy[2]) != null;
                            }
                            catch { }
                            if (!aligned)
                                skipped.Add(view.Name + "|not-orthographic (isometric not dimensioned)");
                            else
                            {
                                var gv = ExtractViewGeometry(view);
                                if (gv.Err != null)
                                    skipped.Add(view.Name + "|geometry read failed: " + gv.Err);
                                else if (gv.Lines.Count == 0 && gv.Circles.Count == 0)
                                    skipped.Add(view.Name + "|no projected geometry");
                                else
                                {
                                    ortho.Add(view);
                                    geomOf[view.Name] = gv;
                                }
                            }
                        }
                    }
                    viewObj = next;
                }

                // Which single view OWNS the hole-position dimensions: the one that shows the most holes
                // as circles. The same holes are plain lines in the neighbouring views, so dimensioning
                // them there prints a second set of values that the reference sheets (Photo 11: one
                // baseline row per direction, ON the circle view) never show.
                string holeView = null;
                int bestCircles = 0;
                foreach (var v in ortho)
                {
                    ViewGeomData gv;
                    if (!geomOf.TryGetValue(v.Name, out gv) || gv == null) continue;
                    if (gv.Circles.Count > bestCircles) { bestCircles = gv.Circles.Count; holeView = v.Name; }
                }
                ExecLog.Write($"essentials: hole-position view = '{holeView}' ({bestCircles} circle(s))");

                foreach (var v in ortho)
                {
                    ViewGeomData gv;
                    geomOf.TryGetValue(v.Name, out gv);
                    bool ownsHoles = holePos && holeView != null && v.Name == holeView;
                    total += EssentialsForView(modelDoc, drawingDoc, v, gap, callouts, circleDiams,
                        ownsHoles, gv, sheetRect, seen, created, skipped);
                }
            }
            finally
            {
                try
                {
                    _solidWorks.SetUserPreferenceToggle(
                        (int)swUserPreferenceToggle_e.swInputDimValOnCreate, prevInputDimVal);
                }
                catch { }
            }

            // Leave the sheet FITTED: annotation picking/creation can leave the graphics view zoomed onto
            // the last dimension, and every preview/render (PNG export) would then show that corner
            // instead of the sheet (measured: the refresh render came back as one arrow head).
            try { modelDoc.ViewZoomtofit2(); } catch { }
            try { modelDoc.GraphicsRedraw2(); } catch { }

            ExecLog.Write($"auto_dimension_drawing essentials: kept {total} of {created.Count} attempt(s).");

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
                    Features = new List<string>(),
                    Dimensions = new List<string>()
                },
                ResultGeometry = new JObject
                {
                    ["mode"] = "essentials",
                    ["inserted_count"] = total,
                    ["gap_mm"] = R6(gap * 1000.0),
                    ["include_callouts"] = callouts,
                    ["include_hole_positions"] = holePos,
                    ["include_circle_diameters"] = circleDiams,
                    ["clear_existing"] = clearExisting,
                    ["created"] = created,
                    ["skipped"] = skipped
                },
                Error = null
            };

            _guard.RegisterCompleted(request.OperationId, response);
            return response;
        }

        // ---------------------------------------------------------------- one-measurement-once
        // 0.1 mm buckets: the envelope measured on two views differs in the last decimals (14.009 vs
        // 14.029 mm) and must still count as the same measurement.
        private static long Bucket(double mm) { return (long)Math.Round(mm * 10.0); }

        private const long DiametricKeyOffset = 1000000000L;

        private static long Bucket(double mm, bool diametric)
        {
            return (long)Math.Round(mm * 10.0) + (diametric ? DiametricKeyOffset : 0L);
        }

        private static bool Claim(Dictionary<long, string> seen, double mm, string viewName, bool diametric = false)
        {
            string owner;
            long key = Bucket(mm, diametric);
            if (seen.TryGetValue(key, out owner) && owner != viewName) return false;
            seen[key] = viewName;
            return true;
        }

        private static void Release(Dictionary<long, string> seen, double mm, string viewName, bool diametric = false)
        {
            string owner;
            long key = Bucket(mm, diametric);
            if (seen.TryGetValue(key, out owner) && owner == viewName) seen.Remove(key);
        }

        // Create one curated dimension unless the same measurement was already dimensioned on ANOTHER
        // view of this sheet, in which case it is reported in skipped[] and nothing is drawn.
        private bool AddCuratedDim(IModelDoc2 modelDoc, Dictionary<long, string> seen, string viewName,
            EntityPick from, EntityPick to, double tx, double ty, JArray created, JArray skipped,
            string kind, double expectedMm, bool selfFallback = false, EntityPick selfPick = null)
        {
            if (!Claim(seen, expectedMm, viewName))
            {
                skipped.Add(kind + "|" + expectedMm.ToString("0.##", CultureInfo.InvariantCulture) +
                    " mm is already dimensioned on another view of this sheet");
                return false;
            }
            if (AddLinearDim(modelDoc, from, to, tx, ty, created, kind, expectedMm)) return true;
            if (selfFallback && selfPick != null &&
                AddSelfDim(modelDoc, selfPick, tx, ty, created, kind + " (self)", expectedMm))
                return true;
            Release(seen, expectedMm, viewName);
            return false;
        }

        // ONE pick instead of two. An inner edge that no vertex pair can reach — a SILHOUETTE, the
        // outline of a turned part's groove neck, has no vertices at its ends at all — still dimensions
        // as its OWN length, which is exactly the extent the caller wants. Same gate as every other
        // dimension: the value is read back from SolidWorks and a dimension that disagrees is deleted.
        private bool AddSelfDim(IModelDoc2 modelDoc, EntityPick pick, double tx, double ty,
            JArray created, string tag, double expectedMm)
        {
            var rec = new JObject
            {
                ["kind"] = tag,
                ["expected_mm"] = R6(expectedMm),
                ["pick_mm"] = new JArray { new JArray { R6(pick.Points[0][0] * 1000.0), R6(pick.Points[0][1] * 1000.0) } }
            };
            try
            {
                modelDoc.ClearSelection2(true);
                double[] hit;
                if (!PickAt(modelDoc, pick, false, tag, out hit))
                {
                    rec["ok"] = false; rec["why"] = "no pick reached an edge for a self dimension";
                    created.Add(rec); return false;
                }
                var d = modelDoc.AddDimension2(tx, ty, 0) as IDisplayDimension;
                double? v = null;
                try
                {
                    var sd = d == null ? null : d.GetDimension2(0) as IDimension;
                    if (sd != null) v = sd.SystemValue * 1000.0;
                }
                catch { }
                if (d != null && v != null && Math.Abs(v.Value - expectedMm) < 0.05)
                {
                    rec["ok"] = true;
                    rec["value_mm"] = R6(v.Value);
                    rec["text_at_mm"] = new JArray(R6(tx * 1000.0), R6(ty * 1000.0));
                    created.Add(rec);
                    return true;
                }
                if (d != null) DeleteDimensionAt(modelDoc, tx, ty);
                rec["ok"] = false;
                rec["why"] = v == null
                    ? "self dimension value unreadable — removed"
                    : "self dimension read " + v.Value.ToString("0.##", CultureInfo.InvariantCulture) +
                      " mm, expected " + expectedMm.ToString("0.##", CultureInfo.InvariantCulture) + " — removed";
                created.Add(rec);
                return false;
            }
            catch (Exception ex)
            {
                rec["ok"] = false; rec["why"] = ex.Message;
                created.Add(rec); return false;
            }
        }

        // Delete every display dimension of the ACTIVE sheet (the curated dimensions and the hole
        // callouts are both display dimensions). Used by clear_existing so a re-run refreshes the sheet.
        private void RemoveSheetDisplayDimensions(IDrawingDoc drawingDoc, IModelDoc2 modelDoc, JArray skipped)
        {
            int removed = 0;
            try
            {
                object vo = drawingDoc.GetFirstView();
                int guard = 0;
                while (vo != null && guard++ < 500)
                {
                    var v = vo as IView;
                    object next = null;
                    if (v != null)
                    {
                        try { next = v.GetNextView(); } catch { }
                        object dim = null;
                        try { dim = v.GetFirstDisplayDimension5(); } catch { }
                        int g2 = 0;
                        while (dim != null && g2++ < 2000)
                        {
                            var dd = dim as IDisplayDimension;
                            object nd = null;
                            try { nd = dd.GetNext3(); } catch { }
                            try
                            {
                                var ann = dd.GetAnnotation() as IAnnotation;
                                if (ann != null && ann.Select3(false, null))
                                {
                                    modelDoc.EditDelete();
                                    removed++;
                                }
                            }
                            catch { }
                            dim = nd;
                        }
                    }
                    vo = next;
                }
            }
            catch (Exception ex) { skipped.Add("clear_existing|" + ex.Message); }
            ExecLog.Write($"essentials clear_existing: removed {removed} display dimension(s).");
        }

        // ---------------------------------------------------------------- per-view essentials
        private int EssentialsForView(IModelDoc2 modelDoc, IDrawingDoc drawingDoc, IView view, double gap,
            bool callouts, bool circleDiams, bool ownsHoles, ViewGeomData g, double[] sheet,
            Dictionary<long, string> seen, JArray created, JArray skipped)
        {
            int made = 0;
            if (g == null) { skipped.Add(view.Name + "|no geometry"); return 0; }

            double scale = 1.0; try { scale = view.ScaleDecimal; } catch { }
            var pos = view.Position as double[];
            if (pos == null || pos.Length < 2) { skipped.Add(view.Name + "|no view position"); return 0; }
            double ox = pos[0], oy = pos[1];
            Func<double, double, double[]> S = (px, py) => new[] { ox + scale * px, oy + scale * py };

            // View-plane axes, for classifying a picked edge's orientation in THIS view (see header).
            double[] fo2, fxAx = null, fyAx = null;
            try { ViewModelFrameRaw(view, out fo2, out fxAx, out fyAx); } catch { }

            // ---- geometry bounding box, view-plane metres
            double pMinX = double.MaxValue, pMaxX = double.MinValue, pMinY = double.MaxValue, pMaxY = double.MinValue;
            Action<double, double> acc = (x, y) =>
            {
                if (x < pMinX) pMinX = x; if (x > pMaxX) pMaxX = x;
                if (y < pMinY) pMinY = y; if (y > pMaxY) pMaxY = y;
            };
            foreach (var l in g.Lines) { acc(l.X1, l.Y1); acc(l.X2, l.Y2); }
            foreach (var c in g.Circles) { acc(c.CX - c.R, c.CY - c.R); acc(c.CX + c.R, c.CY + c.R); }
            double[] bl = S(pMinX, pMinY), tr = S(pMaxX, pMaxY);
            double sMinX = bl[0], sMinY = bl[1], sMaxX = tr[0], sMaxY = tr[1];

            double tol = 1e-6;
            var verts = g.Lines.Where(l => Math.Abs(l.X1 - l.X2) < tol)
                .OrderBy(l => Math.Min(l.X1, l.X2))
                .ThenByDescending(l => Math.Abs(l.Y2 - l.Y1)).ToList();
            var hors = g.Lines.Where(l => Math.Abs(l.Y1 - l.Y2) < tol)
                .OrderBy(l => Math.Min(l.Y1, l.Y2))
                .ThenByDescending(l => Math.Abs(l.X2 - l.X1)).ToList();

            // baseline references: left-most vertical edge, bottom-most horizontal edge
            LineRec vBase = (verts.Count > 0) ? verts.First() : null;
            LineRec hBase = (hors.Count > 0) ? hors.First() : null;

            ExecLog.Write($"essentials view '{view.Name}' scale={scale} pos=({ox * 1000:0.##},{oy * 1000:0.##}) " +
                $"lines={g.Lines.Count} circles={g.Circles.Count} verts={verts.Count} hors={hors.Count} " +
                $"vBase={(vBase == null ? "none" : $"x={vBase.X1 * 1000:0.##} y={vBase.Y1 * 1000:0.##}..{vBase.Y2 * 1000:0.##}")} " +
                $"hBase={(hBase == null ? "none" : $"y={hBase.Y1 * 1000:0.##} x={hBase.X1 * 1000:0.##}..{hBase.X2 * 1000:0.##}")}");

            // ---- placement. Dimension text must stay ON the sheet and outside the view outline, so the
            // stack distance is chosen from the free margin on each side: the classic below/left stack
            // is used when it fits, otherwise it FLIPS to the opposite side instead of running off the
            // paper (an off-sheet dimension is not printed — measured on the first live sheets, where
            // the height and hole-row dims of a 182 mm-wide view at sheet x=8.65 mm landed at x<0).
            double sheetW = (sheet != null && sheet.Length >= 4) ? sheet[2] : 0.297;
            double sheetH = (sheet != null && sheet.Length >= 4) ? sheet[3] : 0.210;
            // Free room on each side is measured to the NEAREST OBSTACLE, not to the paper edge: the
            // neighbour views and the reserved title-block corner are obstacles too. Measuring to the
            // paper is what put 32.5/40.5/424.5/432.5 on top of the front view and 41.5/90/146.5 on
            // top of the right view (measured on Part1_layout.png, 2026-10-20).
            var free = FreeMargins(drawingDoc, view, sMinX, sMinY, sMaxX, sMaxY, sheetW, sheetH, 0.004);
            double mL = free[0], mR = free[1], mB = free[2], mT = free[3];

            // Curated hole coordinates: ONE dimension per DISTINCT X (from the vertical baseline) and per
            // DISTINCT Y (from the horizontal baseline). The count then follows the PATTERN, not the hole
            // count — reference Photo 11 dimensions 4 values on a 22-hole plate, Photo 7 dimensions 12.
            var holeX = new List<double>();
            var holeY = new List<double>();
            if (ownsHoles)
                foreach (var c in g.Circles)
                {
                    if (!holeX.Any(v => Math.Abs(v - c.CX) < 1e-5)) holeX.Add(c.CX);
                    if (!holeY.Any(v => Math.Abs(v - c.CY) < 1e-5)) holeY.Add(c.CY);
                }
            holeX.Sort();
            holeY.Sort();

            double stack = Math.Max(0.006, Math.Min(gap, 0.009));   // pitch between stacked dimensions
            double minText = 0.015;                                 // "406.00" at 3.5 mm text + air

            // HALF SPLIT (client rule, 2026-10-20): a plate is read as two halves — the X dimensions of
            // the holes in the UPPER half of the view go ABOVE it, the ones in the LOWER half go BELOW,
            // so the eye never has to cross the part to find a number. Each side packs its OWN rows.
            double sMidY = (sMinY + sMaxY) / 2.0;
            var holeUp = new List<bool>();
            var hRowOf = new List<int>();
            int rowsBelow = 0, rowsAbove = 0;
            if (ownsHoles && vBase != null)
            {
                foreach (var x in holeX)
                {
                    var cAtX = g.Circles.First(k => Math.Abs(k.CX - x) < 1e-5);
                    holeUp.Add(S(cAtX.CX, cAtX.CY)[1] > sMidY);
                    hRowOf.Add(0);
                }
                // SERIES ORDER (client rule 2026-10-20, taken from his own Auto Grid Cutter sheet
                // 178 / 228 / 323 / 368): one value per row, and the LARGEST value sits on the
                // OUTERMOST row. Below the view the big number is the lowest, above it the highest,
                // so the eye reads from the envelope inwards.
                foreach (bool side in new[] { false, true })
                {
                    var rank = Enumerable.Range(0, holeX.Count)
                        .Where(k => holeUp[k] == side)
                        .OrderByDescending(k => Math.Abs(holeX[k] - vBase.X1))
                        .ToList();
                    for (int r = 0; r < rank.Count; r++) hRowOf[rank[r]] = r;
                    if (side) rowsAbove = rank.Count; else rowsBelow = rank.Count;
                }
            }

            var vMidY = new List<double>();
            var yColOf = new List<int>();
            int colsY = 0;
            if (ownsHoles && hBase != null)
            {
                foreach (var y in holeY)
                {
                    var c = g.Circles.First(k => Math.Abs(k.CY - y) < 1e-5);
                    vMidY.Add((S(c.CX, hBase.Y1)[1] + S(c.CX, c.CY)[1]) / 2.0);
                    yColOf.Add(0);
                }
                var rank = Enumerable.Range(0, holeY.Count)
                    .OrderByDescending(k => Math.Abs(holeY[k] - hBase.Y1)).ToList();
                for (int r = 0; r < rank.Count; r++) yColOf[rank[r]] = r;
                colsY = rank.Count;
            }

            double hNeedBelow = stack * (2 + rowsBelow) + 0.010;
            double hNeedAbove = stack * (1 + rowsAbove) + 0.010;
            double vNeed = stack * (1 + colsY) + 0.010;
            bool below = (mB >= hNeedBelow) || (mB >= mT);
            bool split = rowsAbove > 0 && mT >= hNeedAbove;
            bool leftSide = (mL >= vNeed) || (mL >= mR);
            double vSign = leftSide ? -1.0 : 1.0;

            if (!split && rowsAbove > 0)
            {
                // No room above the view: fall back to the old one-sided stack, re-ranked on one side.
                rowsBelow = rowsBelow + rowsAbove;
                rowsAbove = 0;
                var rank = Enumerable.Range(0, holeX.Count)
                    .OrderByDescending(k => Math.Abs(holeX[k] - vBase.X1)).ToList();
                for (int r = 0; r < rank.Count; r++) hRowOf[rank[r]] = r;
                for (int k = 0; k < holeUp.Count; k++) holeUp[k] = false;
                hNeedBelow = stack * (2 + rowsBelow) + 0.010;
            }

            // Fit the stack pitch to the room actually available on EACH side: a long part on an A4
            // sheet has ~60 mm of margin, and six 9 mm rows would push the outermost dimension past the
            // paper edge (measured on the first live sheets). Floor at 5 mm — below that the layout
            // would be unreadable anyway.
            double hPitchBelow = Math.Min(stack, Math.Max(0.005, (mB - 0.012) / Math.Max(1.0, 1.0 + rowsBelow)));
            double hPitchAbove = Math.Min(stack, Math.Max(0.005, (mT - 0.012) / Math.Max(1.0, 1.0 + rowsAbove)));
            double vRoom = leftSide ? mL : mR;
            double vPitch = Math.Min(stack, Math.Max(0.005, (vRoom - 0.012) / Math.Max(1, 1 + colsY)));
            bool overallAbove = !below;
            double hRowY = below
                ? sMinY - hPitchBelow * (rowsBelow + 1)
                : sMaxY + hPitchAbove * (rowsAbove + 1);
            double vColX = leftSide ? sMinX - vPitch * (colsY + 1) : sMaxX + vPitch * (colsY + 1);
            double hPitch = below ? hPitchBelow : hPitchAbove;
            double hSign = below ? -1.0 : 1.0;
            Func<int, double> holeRowY = (int i) => holeUp[i]
                ? sMaxY + hPitchAbove * (rowsAbove - hRowOf[i])
                : sMinY - hPitchBelow * (rowsBelow - hRowOf[i]);

            ExecLog.Write($"essentials place '{view.Name}' margins L={mL * 1000:0.#} R={mR * 1000:0.#} " +
                $"B={mB * 1000:0.#} T={mT * 1000:0.#} needBelow={hNeedBelow * 1000:0.#} " +
                $"needAbove={hNeedAbove * 1000:0.#} needV={vNeed * 1000:0.#} " +
                $"-> overall={(below ? "below" : "above")} split={(split ? "on" : "off")} " +
                $"yStack={(leftSide ? "left" : "right")} hPitchB={hPitchBelow * 1000:0.#} " +
                $"hPitchA={hPitchAbove * 1000:0.#} vPitch={vPitch * 1000:0.#} " +
                $"holeX={holeX.Count} holeY={holeY.Count} rowsBelow={rowsBelow} rowsAbove={rowsAbove}");

            // Every distinct line endpoint of the view, in view-plane metres. A chamfered outline has no
            // horizontal edge to dimension across (measured on Part3: lines=8 verts=6 hors=0, so the 97 mm
            // height silently disappeared from the sheet) — but those endpoints still reach the bounding
            // box, so the extreme VERTICES carry the same width and height.
            var allVerts = new List<double[]>();
            foreach (var l in g.Lines)
                foreach (var pt in new[] { new[] { l.X1, l.Y1 }, new[] { l.X2, l.Y2 } })
                    if (!allVerts.Any(p => Math.Abs(p[0] - pt[0]) < 1e-5 && Math.Abs(p[1] - pt[1]) < 1e-5))
                        allVerts.Add(pt);

            // ---- 1. overall WIDTH: extreme vertical edges first, then (when those edges do not span the
            // envelope) corner-to-corner between the extreme VERTICES. A rounded end has no vertical edge
            // at the extreme — measured on Part2, whose extreme vertical edges are 700 apart while the part
            // is 813 long — and a wrong pair is caught by the value readback instead of being printed.
            double[] wCentre = S((pMinX + pMaxX) / 2.0, pMinY);
            bool wOk = false;
            if (verts.Count >= 2)
                wOk = AddCuratedDim(modelDoc, seen, view.Name,
                    LinePick(verts.First(), "vline", S, fxAx, fyAx),
                    LinePick(verts.Last(), "vline", S, fxAx, fyAx),
                    wCentre[0], hRowY, created, skipped,
                    view.Name + "|overall width", (pMaxX - pMinX) * 1000.0);
            if (!wOk && allVerts.Count >= 2)
            {
                // The extremes alone are not a dimensionable pair: on Part1's front view the lowest
                // point is a notch at x=+126 while the highest is the outline end at x=-203, so
                // bottom-most-to-top-most measured 406 mm across the sheet and was deleted by the
                // readback. Prefer a pair that shares the other axis; keep the old extremes only when
                // no aligned pair exists at all.
                var wpair = AlignedExtremePair(allVerts, 0) ?? new[]
                {
                    allVerts.OrderBy(p => p[0]).First(),
                    allVerts.OrderByDescending(p => p[0]).First()
                };
                wOk = AddCuratedDim(modelDoc, seen, view.Name,
                    VertexPick(wpair[0], S, fxAx, fyAx),
                    VertexPick(wpair[1], S, fxAx, fyAx),
                    wCentre[0], hRowY, created, skipped,
                    view.Name + "|overall width (corners)", (pMaxX - pMinX) * 1000.0);
            }
            if (wOk) made++;
            else if (verts.Count < 2 && allVerts.Count < 2)
                skipped.Add(view.Name + "|no edge and no vertex for overall width");

            // ---- 2. overall HEIGHT: the same two stages on the vertical extent.
            double[] hCentre = S(pMinX, (pMinY + pMaxY) / 2.0);
            bool hOk = false;
            if (hors.Count >= 2)
                hOk = AddCuratedDim(modelDoc, seen, view.Name,
                    LinePick(hors.First(), "hline", S, fxAx, fyAx),
                    LinePick(hors.Last(), "hline", S, fxAx, fyAx),
                    vColX, hCentre[1], created, skipped,
                    view.Name + "|overall height", (pMaxY - pMinY) * 1000.0);
            if (!hOk && allVerts.Count >= 2)
            {
                var hpair = AlignedExtremePair(allVerts, 1) ?? new[]
                {
                    allVerts.OrderBy(p => p[1]).First(),
                    allVerts.OrderByDescending(p => p[1]).First()
                };
                hOk = AddCuratedDim(modelDoc, seen, view.Name,
                    VertexPick(hpair[0], S, fxAx, fyAx),
                    VertexPick(hpair[1], S, fxAx, fyAx),
                    vColX, hCentre[1], created, skipped,
                    view.Name + "|overall height (corners)", (pMaxY - pMinY) * 1000.0);
            }
            if (hOk) made++;
            else if (hors.Count < 2 && allVerts.Count < 2)
                skipped.Add(view.Name + "|no edge and no vertex for overall height");

            // ---- 3. hole callouts, one per DISTINCT diameter, on the HOLE-OWNING view only (see above):
            // a neighbouring view projects the same holes as plain lines, and its only circles are corner
            // fillets — calling those out printed "Ø5.00" on an end view that has no hole at all.
            //
            // The text side and the 'usedY' ladder are hoisted OUT of this branch on purpose: pass 3b
            // (verified circle diameters) places Ø dimensions from the same side, and the two passes must
            // share one ladder so a callout and a diameter can never land on the same point.
            bool calloutRight = leftSide ? ((mR >= 0.030) || (mR >= mL)) : ((mL >= 0.030) && (mL >= mR));
            double cTextX = calloutRight ? sMaxX + 0.014 : sMinX - 0.014;
            if (!calloutRight && leftSide) cTextX -= vPitch * (holeY.Count + 1.0);
            var usedY = new List<double>();
            var annotatedD = new List<double>();     // diameters already dimensioned in THIS view, mm

            if (callouts && ownsHoles && g.Circles.Count > 0)
            {
                var diameters = new List<double>();
                foreach (var c in g.Circles.OrderByDescending(c => c.R))
                {
                    double d = 2.0 * c.R;
                    if (!diameters.Any(x => Math.Abs(x - d) < 0.0002)) diameters.Add(d);
                }

                // A TURNED PROFILE: every circle of the view sits on one centre (a cylinder with its
                // bores). A hole callout there prints the FEATURE's dimensions, not the hole's (measured
                // on Part3: "Ø96.00", "Ø36.00", "⌴24.50" on a Ø55 x 96 roller), so the diameters are
                // dimensioned directly instead — which is what a turned-part sheet shows anyway.
                bool turnedProfile = g.Circles.Count > 1 && g.Circles.All(x =>
                    Math.Abs(x.CX - g.Circles[0].CX) < 1e-5 && Math.Abs(x.CY - g.Circles[0].CY) < 1e-5);

                foreach (var d in diameters)
                {
                    // Attach the callout to the hole of this diameter CLOSEST to the text (the extreme
                    // hole in the text's direction), so the leader is short and never crosses the part.
                    var sameD = g.Circles.Where(x => Math.Abs(2.0 * x.R - d) < 0.0002).ToList();
                    var c = calloutRight
                        ? sameD.OrderByDescending(x => x.CX).First()
                        : sameD.OrderBy(x => x.CX).First();
                    int n = sameD.Count;
                    string tag = view.Name + (turnedProfile ? "|Ø" : "|callout ") +
                        (n > 1 ? n + "x" : "") +
                        "Ø" + (d * 1000.0).ToString("0.##", CultureInfo.InvariantCulture);
                    double cTextY = S(c.CX, c.CY)[1];
                    while (usedY.Any(v => Math.Abs(v - cTextY) < 0.007)) cTextY += 0.007;
                    usedY.Add(cTextY);
                    double dMm = d * 1000.0;
                    if (!Claim(seen, dMm, view.Name, true))
                    {
                        skipped.Add(tag + "|" + dMm.ToString("0.##", CultureInfo.InvariantCulture) +
                            " already called out on another view of this sheet");
                        continue;
                    }
                    if (AddEssentialsHoleCallout(modelDoc, drawingDoc,
                            CirclePick(c, S, fxAx, fyAx), cTextX, cTextY, created, tag, turnedProfile,
                            view, c))
                    {
                        made++;
                        annotatedD.Add(dMm);
                    }
                    else Release(seen, dMm, view.Name, true);
                }
            }

            // ---- 3b. VERIFIED CIRCLE DIAMETERS. The callout pass only touches the hole-owning view, so a
            // Ø that exists ONLY in another view — a small hole seen end-on, a turned profile's steps — used
            // to need a hand-placed dimension (the client drew Part1's Ø4 by hand for exactly this reason).
            // Every circle still unannotated is dimensioned here the way add_drawing_dimension's circle path
            // does it: pick the arc by its RADIUS, add a DIAMETER dimension, read the value back from the
            // geometry and DELETE it unless it matches. No caller value is ever written on this path, so it
            // can only print a true number.
            if (circleDiams && g.Circles.Count > 0)
            {
                var diam = new List<double>();
                foreach (var c in g.Circles.OrderByDescending(c => c.R))
                {
                    double dd = 2.0 * c.R;
                    if (!diam.Any(x => Math.Abs(x - dd) < 0.0002)) diam.Add(dd);
                }
                foreach (var d in diam)
                {
                    double dMm = d * 1000.0;
                    if (annotatedD.Any(x => Math.Abs(x - dMm) < 0.05)) continue;   // already printed by a callout
                    var same = g.Circles.Where(x => Math.Abs(2.0 * x.R - d) < 0.0002).ToList();
                    var c = calloutRight
                        ? same.OrderByDescending(x => x.CX).First()
                        : same.OrderBy(x => x.CX).First();
                    string tag = view.Name + "|Ø" +
                        dMm.ToString("0.##", CultureInfo.InvariantCulture);

                    // A CORNER ARC touches the view's bounding box in BOTH axes and has no other circle on
                    // its centre: that is a rounded corner/fillet, not a hole — a Ø on it is the measured
                    // "Ø5.00 on an end view with no hole at all" defect. A turned profile is exempt: its
                    // outermost circle IS the silhouette, but it is concentric with the bore circles.
                    bool concentric = g.Circles.Count(k =>
                        Math.Abs(k.CX - c.CX) < 1e-5 && Math.Abs(k.CY - c.CY) < 1e-5) > 1;
                    bool touchesX = Math.Abs(c.CX - c.R - pMinX) < 1e-4 ||
                                    Math.Abs(c.CX + c.R - pMaxX) < 1e-4;
                    bool touchesY = Math.Abs(c.CY - c.R - pMinY) < 1e-4 ||
                                    Math.Abs(c.CY + c.R - pMaxY) < 1e-4;
                    if (!concentric && touchesX && touchesY)
                    {
                        skipped.Add(tag + "|corner arc/fillet (not a hole) — not dimensioned");
                        continue;
                    }
                    if (!Claim(seen, dMm, view.Name, true))
                    {
                        skipped.Add(tag + "|" + dMm.ToString("0.##", CultureInfo.InvariantCulture) +
                            " already dimensioned on another view of this sheet");
                        continue;
                    }
                    double cTextY = S(c.CX, c.CY)[1];
                    while (usedY.Any(v => Math.Abs(v - cTextY) < 0.007)) cTextY += 0.007;
                    usedY.Add(cTextY);
                    if (AddEssentialsHoleCallout(modelDoc, drawingDoc,
                            CirclePick(c, S, fxAx, fyAx), cTextX, cTextY, created, tag, true,
                            view, c))
                    {
                        made++;
                        annotatedD.Add(dMm);
                    }
                    else Release(seen, dMm, view.Name, true);
                }
            }

            // ---- 4. X positions, CHAIN style (company requirement 2026-10-20): one hole to the NEXT
            // hole in the same row, so a machinist walks from hole to hole. Holes that share a row
            // and a diameter are dimensioned ONCE by the distance between them (measured on Part2:
            // the two Ø12.5 holes at ±120 are printed as 240, not as two baseline values).
            // A fresh claim map is required: a chain legitimately prints the same value twice
            // (20 / 85 / 80 / 330 / 80 / 85 / 20), and the sheet-wide "one measurement once" filter
            // would silently swallow the repeat.
            if (ownsHoles && vBase != null && g.Circles.Count > 0)
            {
                var chainSeen = new Dictionary<long, string>();
                var rowsC = g.Circles
                    .GroupBy(k => Math.Round(k.CY, 5))
                    .Select(gr => gr.OrderBy(k => k.CX).ToList())
                    .OrderByDescending(gr => gr[0].CY)
                    .ToList();
                int rowIdx = 0;
                foreach (var row in rowsC)
                {
                    bool up = S(row[0].CX, row[0].CY)[1] > sMidY;
                    double rowY = up
                        ? sMaxY + hPitchAbove * (2 + rowIdx % 3)
                        : sMinY - hPitchBelow * (2 + rowIdx % 3);
                    bool equalPair = row.Count == 2 && Math.Abs(row[0].R - row[1].R) < 1e-6;
                    if (equalPair)
                    {
                        double exp = Math.Abs(row[1].CX - row[0].CX) * 1000.0;
                        double[] mid = S((row[0].CX + row[1].CX) / 2.0, row[0].CY);
                        if (AddCuratedDim(modelDoc, chainSeen, view.Name,
                                CirclePick(row[0], S, fxAx, fyAx), CirclePick(row[1], S, fxAx, fyAx),
                                mid[0], rowY, created, skipped, view.Name + "|chain pair", exp))
                            made++;
                    }
                    else
                    {
                        for (int i = 0; i < row.Count; i++)
                        {
                            var c = row[i];
                            double fromX = i == 0 ? vBase.X1 : row[i - 1].CX;
                            EntityPick fromPick = i == 0
                                ? LinePick(vBase, "vline", S, fxAx, fyAx)
                                : CirclePick(row[i - 1], S, fxAx, fyAx);
                            double exp = Math.Abs(c.CX - fromX) * 1000.0;
                            double[] mid = S((fromX + c.CX) / 2.0, c.CY);
                            string tag = view.Name + "|chain X " + (c.CX * 1000.0)
                                .ToString("0.00", CultureInfo.InvariantCulture);
                            if (AddCuratedDim(modelDoc, chainSeen, view.Name, fromPick,
                                    CirclePick(c, S, fxAx, fyAx),
                                    mid[0], rowY, created, skipped, tag, exp))
                                made++;
                        }
                    }
                    rowIdx++;
                }
            }

            // ---- 5. Y positions, CHAIN style: bottom edge -> lowest hole, then hole to hole upward.
            // Same fresh claim map as X: 10 and 30 legitimately repeat in the vertical chain.
            if (ownsHoles && hBase != null && holeY.Count > 0)
            {
                var chainSeenY = new Dictionary<long, string>();
                for (int i = 0; i < holeY.Count; i++)
                {
                    double y = holeY[i];
                    var c = g.Circles.First(k => Math.Abs(k.CY - y) < 1e-5);
                    double fromY = i == 0 ? hBase.Y1 : holeY[i - 1];
                    EntityPick fromPick = i == 0
                        ? LinePick(hBase, "hline", S, fxAx, fyAx)
                        : CirclePick(g.Circles.First(k => Math.Abs(k.CY - fromY) < 1e-5), S, fxAx, fyAx);
                    string tag = view.Name + "|chain Y " + (y * 1000.0)
                        .ToString("0.00", CultureInfo.InvariantCulture);
                    if (AddCuratedDim(modelDoc, chainSeenY, view.Name, fromPick,
                            CirclePick(c, S, fxAx, fyAx),
                            (leftSide ? sMinX - vPitch * (colsY - yColOf[i])
                                      : sMaxX + vPitch * (colsY - yColOf[i])), vMidY[i], created, skipped, tag,
                            Math.Abs(y - fromY) * 1000.0))
                        made++;
                }
            }

            // ---- 6. inner chain: a view that produced nothing still has inner edges to dimension
            // (offset from envelope, span between inner lines, extent of those lines).
            if (made == 0)
            {
                var innerX = new List<double>();
                foreach (var l in g.Lines)
                {
                    if (Math.Abs(l.X1 - l.X2) > 1e-6) continue;      // vertical only
                    if (Math.Abs(l.Y2 - l.Y1) < 1e-6) continue;      // not a point
                    if (l.X1 <= pMinX + 1e-6 || l.X1 >= pMaxX - 1e-6) continue;
                    if (!innerX.Any(x => Math.Abs(x - l.X1) < 1e-6)) innerX.Add(l.X1);
                }
                innerX.Sort();
                var innerLines = g.Lines.Where(l => Math.Abs(l.X1 - l.X2) < 1e-6
                        && Math.Abs(l.Y2 - l.Y1) > 1e-6
                        && innerX.Any(x => Math.Abs(x - l.X1) < 1e-6))
                    .OrderBy(l => l.X1).ToList();
                ExecLog.Write($"essentials inner '{view.Name}' innerX={innerX.Count} lines={innerLines.Count}");

                if (innerLines.Count >= 2)
                {
                    double x0 = innerX[0], x1 = innerX[innerX.Count - 1];
                    var l0 = innerLines.First();
                    var l1 = innerLines.Last();
                    double a0 = Math.Min(l0.Y1, l0.Y2), a1 = Math.Min(l1.Y1, l1.Y2);
                    double b0 = Math.Max(l0.Y1, l0.Y2), b1 = Math.Max(l1.Y1, l1.Y2);
                    bool atBottom = Math.Abs(a0 - a1) <= Math.Abs(b0 - b1);
                    double y0 = atBottom ? a0 : b0, y1 = atBottom ? a1 : b1;
                    bool spanOk = Math.Abs(y0 - y1) < 1e-4 && AddCuratedDim(modelDoc, seen, view.Name,
                            VertexPick(new[] { x0, y0 }, S, fxAx, fyAx),
                            VertexPick(new[] { x1, y1 }, S, fxAx, fyAx),
                            S((x0 + x1) / 2.0, pMinY)[0], hRowY, created, skipped,
                            view.Name + "|inner span", (x1 - x0) * 1000.0);
                    if (!spanOk)
                        spanOk = AddCuratedDim(modelDoc, seen, view.Name,
                            LinePick(l0, "vline", S, fxAx, fyAx),
                            LinePick(l1, "vline", S, fxAx, fyAx),
                            S((x0 + x1) / 2.0, pMinY)[0], hRowY, created, skipped,
                            view.Name + "|inner span (lines)", (x1 - x0) * 1000.0);
                    if (spanOk) made++;
                }

                if (innerLines.Count >= 1 && verts.Count >= 2)
                {
                    double x0 = innerX[0], x1 = innerX[innerX.Count - 1];
                    double leftOff = x0 - pMinX, rightOff = pMaxX - x1;
                    bool useLeft = leftOff <= rightOff;
                    double off = useLeft ? leftOff : rightOff;
                    var inner = useLeft ? innerLines.First() : innerLines.Last();
                    var edge = useLeft ? verts.First() : verts.Last();
                    var iv = new[] { new[] { inner.X1, inner.Y1 }, new[] { inner.X2, inner.Y2 } };
                    var ev = new[] { new[] { edge.X1, edge.Y1 }, new[] { edge.X2, edge.Y2 } };
                    int bi = 0, bj = 0;
                    double best = double.MaxValue;
                    for (int i = 0; i < 2; i++)
                        for (int k = 0; k < 2; k++)
                        {
                            double d = Math.Abs(iv[i][1] - ev[k][1]);
                            if (d < best) { best = d; bi = i; bj = k; }
                        }
                    bool offOk = off > 1e-4 && best < 1e-3 && AddCuratedDim(modelDoc, seen, view.Name,
                            VertexPick(iv[bi], S, fxAx, fyAx),
                            VertexPick(ev[bj], S, fxAx, fyAx),
                            S(useLeft ? (pMinX + x0) / 2.0 : (x1 + pMaxX) / 2.0, pMinY)[0],
                            hRowY + hSign * hPitch, created, skipped,
                            view.Name + "|inner offset", off * 1000.0);
                    if (!offOk && off > 1e-4)
                        offOk = AddCuratedDim(modelDoc, seen, view.Name,
                            LinePick(edge, "vline", S, fxAx, fyAx),
                            LinePick(inner, "vline", S, fxAx, fyAx),
                            S(useLeft ? (pMinX + x0) / 2.0 : (x1 + pMaxX) / 2.0, pMinY)[0],
                            hRowY + hSign * hPitch, created, skipped,
                            view.Name + "|inner offset (lines)", off * 1000.0);
                    if (offOk) made++;
                }

                if (innerLines.Count >= 1)
                {
                    var longest = innerLines.OrderByDescending(k => Math.Abs(k.Y2 - k.Y1)).First();
                    double ext = Math.Abs(longest.Y2 - longest.Y1) * 1000.0;
                    if (ext > 1e-4 && AddCuratedDim(modelDoc, seen, view.Name,
                            VertexPick(new[] { longest.X1, longest.Y1 }, S, fxAx, fyAx),
                            VertexPick(new[] { longest.X2, longest.Y2 }, S, fxAx, fyAx),
                            vColX, S(longest.X1, (longest.Y1 + longest.Y2) / 2.0)[1], created, skipped,
                            view.Name + "|inner extent", ext,
                            selfFallback: true,
                            selfPick: LinePick(longest, "vline", S, fxAx, fyAx)))
                        made++;
                }
            }

            return made;
        }

        // First-fit packing of dimension texts onto parallel rows: two texts in one row must be at least
        // minText apart, otherwise the next row is used. Returns the row index for `mid`.
        private static int PackRow(List<double> rowEnd, double mid, double minText)
        {
            for (int r = 0; r < rowEnd.Count; r++)
                if (mid - rowEnd[r] >= minText) { rowEnd[r] = mid; return r; }
            rowEnd.Add(mid);
            return rowEnd.Count - 1;
        }

        // ---------------------------------------------------------------- pick descriptors
        // An axis-aligned pair of vertices is the only pair that can carry an envelope dimension:
        // bottom-most and top-most often do not share X (a notch is lower than the outline end), and
        // the diagonal between them measures a different, meaningless distance.
        private static double[][] AlignedExtremePair(List<double[]> pts, int axis, double tol = 1e-4)
        {
            double[][] best = null;
            double bestSpan = -1.0;
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                {
                    if (Math.Abs(pts[i][1 - axis] - pts[j][1 - axis]) > tol) continue;
                    double span = Math.Abs(pts[i][axis] - pts[j][axis]);
                    if (span > bestSpan) { bestSpan = span; best = new[] { pts[i], pts[j] }; }
                }
            return best;
        }

        private EntityPick LinePick(LineRec l, string expect, Func<double, double, double[]> S,
            double[] xdir, double[] ydir)
        {
            // 50% first, then spread outward — never at an endpoint (a corner returns the other edge)
            double[] ts = { 0.5, 0.35, 0.65 };
            double[] v1 = S(l.X1, l.Y1), v2 = S(l.X2, l.Y2);
            bool vertical = Math.Abs(l.X1 - l.X2) < Math.Abs(l.Y1 - l.Y2);
            // Corner fallback: for a vertical line the useful corner is the BOTTOM one, for a horizontal
            // line the LEFT one — so a width falls back to bottom-left/bottom-right and a height to
            // left-bottom/left-top, keeping the two picked corners aligned on one axis.
            double[][] vc = vertical
                ? (v1[1] <= v2[1] ? new[] { v1, v2 } : new[] { v2, v1 })
                : (v1[0] <= v2[0] ? new[] { v1, v2 } : new[] { v2, v1 });
            return new EntityPick
            {
                Expect = expect,
                ExpectR = 0.0,
                Points = ts.Select(t => S(l.X1 + (l.X2 - l.X1) * t, l.Y1 + (l.Y2 - l.Y1) * t)).ToArray(),
                VertexPoints = vc,
                XDir = xdir,
                YDir = ydir
            };
        }

        // A pick that resolves only to a VERTEX. PickAt tries the EDGE path first (which cannot succeed
        // on a bare corner) and then the VERTEX path, so the same point goes in both lists.
        private EntityPick VertexPick(double[] viewPt, Func<double, double, double[]> S,
            double[] xdir, double[] ydir)
        {
            double[] p = S(viewPt[0], viewPt[1]);
            return new EntityPick
            {
                Expect = "vertex",
                ExpectR = 0.0,
                XDir = xdir,
                YDir = ydir,
                Points = new[] { p },
                VertexPoints = new[] { p }
            };
        }

        private EntityPick CirclePick(CircleRec c, Func<double, double, double[]> S,
            double[] xdir, double[] ydir)
        {
            double[] angs = { 0, 30, -30, 60, -60 };
            return new EntityPick
            {
                Expect = "circle",
                ExpectR = c.R,
                XDir = xdir,
                YDir = ydir,
                Points = angs.Select(a =>
                {
                    double r = a * Math.PI / 180.0;
                    return S(c.CX + c.R * Math.Cos(r), c.CY + c.R * Math.Sin(r));
                }).ToArray()
            };
        }

        // ---------------------------------------------------------------- primitives

        // Two-entity linear dimension (line<->line, line<->circle, circle<->circle). Both entities come
        // from the same view and are VERIFIED; the text point is where SolidWorks puts the dimension
        // line. The created value is read back; a disagreeing value means a pick probed onto the wrong
        // entity, so the dimension is deleted instead of left to print a wrong number.
        private bool AddLinearDim(IModelDoc2 modelDoc, EntityPick a, EntityPick b,
            double tx, double ty, JArray created, string tag, double expectedMm)
        {
            var rec = new JObject
            {
                ["kind"] = tag,
                ["expected_mm"] = R6(expectedMm),
                ["pick_mm"] = new JArray
                {
                    new JArray { R6(a.Points[0][0] * 1000.0), R6(a.Points[0][1] * 1000.0) },
                    new JArray { R6(b.Points[0][0] * 1000.0), R6(b.Points[0][1] * 1000.0) }
                }
            };
            try
            {
                // Up to two attempts, the second with the pick order REVERSED, each starting from a
                // cleared selection. A probe that misses can silently drop a pick already made, and the
                // pairwise dimension then degenerates into a single-entity one: a line<->circle distance
                // came back as the circle's DIAMETER ("38.00" printed as "6.00" = the Ø6 of that hole)
                // and a line<->line distance as one line's LENGTH ("406.00" printed as "151.00"). The
                // value readback catches both and the dimension is DELETED; re-picking both entities
                // from scratch is what recovers it.
                // Never "repair" the selection by re-selecting the first entity instead:
                // SelectByID2 with Append=true TOGGLES an already-selected entity OFF (measured - that
                // repair turned EVERY dimension on the sheet into a single-entity one).
                IDisplayDimension dim = null;
                double? got = null;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    EntityPick p1 = (attempt == 0) ? a : b;
                    EntityPick p2 = (attempt == 0) ? b : a;
                    string ord = (attempt == 0) ? "#1/#2" : "#2/#1";
                    modelDoc.ClearSelection2(true);
                    double[] h1;
                    if (!PickAt(modelDoc, p1, false, tag + " " + ord + " first", out h1)) continue;
                    double[] h2;
                    if (!PickAt(modelDoc, p2, true, tag + " " + ord + " second", out h2)) continue;
                    var d = modelDoc.AddDimension2(tx, ty, 0) as IDisplayDimension;
                    if (d == null) continue;
                    double? v = null;
                    try
                    {
                        var sd = d.GetDimension2(0) as IDimension;
                        if (sd != null) v = sd.SystemValue * 1000.0;
                    }
                    catch { }
                    if (v == null)
                    {
                        // A dimension whose value cannot be read back is a number nobody checked — it
                        // goes, even if its geometry may be right. Imperfection is acceptable, a wrong
                        // number on a client sheet is not.
                        ExecLog.Write($"essentials retry [{tag}] attempt {attempt + 1} ({ord}) value unreadable - dimension removed");
                        DeleteDimensionAt(modelDoc, tx, ty);
                        continue;
                    }
                    if (Math.Abs(v.Value - expectedMm) < 0.05) { dim = d; got = v; break; }
                    ExecLog.Write($"essentials retry [{tag}] attempt {attempt + 1} ({ord}) produced " +
                        $"{v.Value:0.####} mm, expected {expectedMm:0.####} mm - dimension removed");
                    DeleteDimensionAt(modelDoc, tx, ty);
                }
                if (dim == null)
                {
                    rec["ok"] = false;
                    rec["why"] = "no pick pair produced the expected value — dimension removed";
                    created.Add(rec); return false;
                }
                rec["value_mm"] = R6(got.Value);
                rec["match"] = true;
                rec["ok"] = true;
                created.Add(rec);
                return true;
            }
            catch (Exception ex)
            {
                rec["ok"] = false; rec["why"] = ex.Message;
                created.Add(rec); return false;
            }
        }

        private bool AddEssentialsHoleCallout(IModelDoc2 modelDoc, IDrawingDoc drawingDoc,
            EntityPick pick, double textX, double textY, JArray created, string tag,
            bool forceDiameter = false, IView view = null, CircleRec circle = null)
        {
            var rec = new JObject { ["kind"] = tag };
            try
            {
                // Select the circular EDGE: by geometry when the caller knows which extracted circle this
                // is (no hit-test tolerance, so a 1.2 mm-on-paper hole works), otherwise by coordinate.
                Func<string, bool> selectCircle = (tg) =>
                {
                    modelDoc.ClearSelection2(true);
                    if (view != null && circle != null && TrySelectCircleEntity(modelDoc, view, circle))
                        return true;
                    double[] hh;
                    return PickAt(modelDoc, pick, false, tg, out hh);
                };

                if (!selectCircle(tag))
                {
                    rec["ok"] = false; rec["why"] = "no circular edge of this diameter at any pick point";
                    created.Add(rec); return false;
                }
                // The circular edge stays SELECTED; (textX,textY) is where the callout TEXT is written, so
                // it is placed clear of the view outline instead of on top of its own hole.
                object co = forceDiameter ? null : drawingDoc.AddHoleCallout2(textX, textY, 0.0);
                double? coVal = null;
                try
                {
                    var cd = co as IDisplayDimension;
                    var sd = cd == null ? null : cd.GetDimension2(0) as IDimension;
                    if (sd != null) coVal = sd.SystemValue * 1000.0;
                }
                catch { }
                double want = pick.ExpectR * 2000.0;      // the diameter the extracted circle has
                if (co != null && coVal != null && Math.Abs(coVal.Value - want) < 0.05)
                {
                    rec["ok"] = true;
                    rec["value_mm"] = R6(coVal.Value);
                    rec["text_at_mm"] = new JArray(R6(textX * 1000.0), R6(textY * 1000.0));
                    created.Add(rec);
                    return true;
                }
                // Nothing unverified may print. A callout that could not be read (or that disagrees with
                // the picked circle) is removed and a verified DIAMETER dimension takes its place on the
                // same circle at the same text point.
                if (co != null)
                    ExecLog.Write($"essentials callout [{tag}] " + (coVal == null
                        ? "value unreadable" : $"printed {coVal.Value:0.##} mm") +
                        $", expected Ø{want:0.##} - replaced by a diameter dimension");
                if (co != null) DeleteDimensionAt(modelDoc, textX, textY);
                if (!forceDiameter)
                {
                    if (!selectCircle(tag + " diameter"))
                    {
                        rec["ok"] = false; rec["why"] = "no circular edge for a diameter dimension";
                        created.Add(rec); return false;
                    }
                }
                var ddim = modelDoc.AddDimension2(textX, textY, 0) as IDisplayDimension;
                double? dv = null;
                try
                {
                    var sd = ddim == null ? null : ddim.GetDimension2(0) as IDimension;
                    if (sd != null) dv = sd.SystemValue * 1000.0;
                }
                catch { }
                if (ddim != null && dv != null && Math.Abs(dv.Value - want) < 0.05)
                {
                    rec["ok"] = true;
                    rec["value_mm"] = R6(dv.Value);
                    rec["text_at_mm"] = new JArray(R6(textX * 1000.0), R6(textY * 1000.0));
                    created.Add(rec);
                    return true;
                }
                if (ddim != null) DeleteDimensionAt(modelDoc, textX, textY);
                rec["ok"] = false;
                rec["why"] = "no verifiable diameter annotation (wanted Ø" +
                    want.ToString("0.##", CultureInfo.InvariantCulture) + ")";
                created.Add(rec);
                return false;
            }
            catch (Exception ex)
            {
                rec["ok"] = false; rec["why"] = ex.Message;
                created.Add(rec); return false;
            }
        }

        private void DeleteDimensionAt(IModelDoc2 modelDoc, double tx, double ty)
        {
            try
            {
                modelDoc.ClearSelection2(true);
                if (modelDoc.Extension.SelectByID2("", "DIMENSION", tx, ty, 0, false, 0, null, 0))
                    modelDoc.EditDelete();
            }
            catch { }
        }

        // Try every candidate point (with a small positional micro-probe around each), VERIFY the entity
        // is the one asked for, and roll a rejected probe back (keeping any earlier valid selection).
        private bool PickAt(IModelDoc2 modelDoc, EntityPick pick, bool append, string tag, out double[] hit)
        {
            hit = null;
            double[] radii = { 0.0, 0.0003 };
            double[][] dirs =
            {
                new[] { 0.0, 0.0 },
                new[] { 0.0, 1.0 }, new[] { 0.0, -1.0 }, new[] { 1.0, 0.0 }, new[] { -1.0, 0.0 }
            };
            // A vertex-expected pick never resolves through the EDGE path (a bare corner is not an edge),
            // so skip those attempts instead of burning ~10 selects before the corner fallback below.
            // EDGE reaches projected model edges; SILHOUETTE reaches the outline of a curved wall — a
            // turned part's barrel or the neck of a groove — which EDGE can never select and which
            // carries no vertices either. Measured on the roller's front view (Part3, Drawing View5):
            // every EDGE probe on the groove neck MISSED and a SILHOUETTE probe at the same point hit.
            string[] types = (pick.Expect == "vertex") ? new string[0] : new[] { "EDGE", "SILHOUETTE" };
            int misses = 0;
            foreach (var p in pick.Points)
                foreach (var r in radii)
                    foreach (var d in dirs)
                        foreach (var t in types)
                        {
                            double px = p[0] + r * d[0], py = p[1] + r * d[1];
                            string whats;
                            if (!SelectOne(modelDoc, t, px, py, append, out whats))
                            {
                                // Logged because a MISSING hit is not harmless: it can drop a selection
                                // already made (see AddLinearDim), and without this line the roll-back
                                // looks like it never happened.
                                if (misses++ < 6)
                                    ExecLog.Write($"essentials pick MISS [{tag}] ({px * 1000:0.##},{py * 1000:0.##}) [{t}] {whats}");
                                continue;
                            }
                            var info = DescribePickedEdge(modelDoc, pick.XDir, pick.YDir);
                            if (MatchesExpect(info, pick.Expect, pick.ExpectR))
                            {
                                ExecLog.Write($"essentials pick OK [{tag}] {info.Text} [{t}]");
                                hit = new[] { px, py };
                                return true;
                            }
                            ExecLog.Write($"essentials pick WRONG [{tag}] {info.Text} [{t}] " +
                                $"at ({px * 1000:0.##},{py * 1000:0.##}) wanted {pick.Expect}" + whats);
                            DeselectLast(modelDoc, append);
                        }

            // Corner fallback: on some views the extreme boundary edge cannot be picked as an edge at
            // all — the hit test keeps resolving to the perpendicular edge — but its corner VERTEX can.
            if (pick.VertexPoints != null)
                foreach (var p in pick.VertexPoints)
                    foreach (var r in radii)
                        foreach (var d in dirs)
                            foreach (var vt in new[] { "VERTEX", "SKETCHPOINT", "EXTSKETCHPOINT" })
                            {
                                double px = p[0] + r * d[0], py = p[1] + r * d[1];
                                if (!SelectOne(modelDoc, vt, px, py, append, out _)) continue;
                                var vinfo = DescribePickedEdge(modelDoc, pick.XDir, pick.YDir);
                                if (MatchesExpect(vinfo, "vertex", 0.0))
                                {
                                    ExecLog.Write($"essentials pick OK [{tag}] {vinfo.Text} " +
                                        $"at ({px * 1000:0.##},{py * 1000:0.##}) [{vt}]");
                                    hit = new[] { px, py };
                                    return true;
                                }
                                DeselectLast(modelDoc, append);
                            }

            ExecLog.Write($"essentials pick FAILED [{tag}] wanted {pick.Expect} at " +
                $"({pick.Points[0][0] * 1000:0.##},{pick.Points[0][1] * 1000:0.##})");
            return false;
        }

        private bool SelectOne(IModelDoc2 modelDoc, string type, double x, double y, bool append, out string whats)
        {
            whats = "";
            try
            {
                return modelDoc.Extension.SelectByID2("", type, x, y, 0, append, 0, null, 0);
            }
            catch (Exception ex) { whats = " err:" + ex.Message; return false; }
        }

        private void DeselectLast(IModelDoc2 modelDoc, bool appended)
        {
            try
            {
                if (!appended) { modelDoc.ClearSelection2(true); return; }
                var sm = modelDoc.SelectionManager as ISelectionMgr;
                if (sm == null) { modelDoc.ClearSelection2(true); return; }
                int n = sm.GetSelectedObjectCount2(-1);
                if (n > 0) sm.DeSelect2(n, -1);
            }
            catch
            {
                try { modelDoc.ClearSelection2(true); } catch { }
            }
        }

        private PickInfo DescribePickedEdge(IModelDoc2 modelDoc, double[] xdir, double[] ydir)
        {
            var pi = new PickInfo();
            try
            {
                var sm = modelDoc.SelectionManager as ISelectionMgr;
                if (sm == null) { pi.Text = "no-selection-manager"; return pi; }
                int n = sm.GetSelectedObjectCount2(-1);
                if (n < 1) { pi.Text = "nothing-selected"; return pi; }
                object o = sm.GetSelectedObject6(n, -1);
                var vertex = o as IVertex;
                if (vertex != null) { pi.IsVertex = true; pi.Text = "VERTEX"; return pi; }
                // A corner that SolidWorks exposes as a sketch point rather than a model vertex (a
                // projected tangent/outline endpoint on a turned part). It dimensions corner-to-corner
                // exactly like a vertex.
                var skp = o as ISketchPoint;
                if (skp != null) { pi.IsSketchPoint = true; pi.Text = "SKETCHPOINT"; return pi; }
                // A drawing SILHOUETTE edge: the outline of a curved wall (a turned part's barrel, or the
                // neck of a groove). It is not an IEdge, so an EDGE probe can never reach it, and it
                // carries no vertices either — its own two endpoints are the only geometry it exposes.
                var sil = o as ISilhouetteEdge;
                if (sil != null)
                {
                    double[] s1 = null, s2 = null;
                    try
                    {
                        var m1 = sil.GetStartPoint() as IMathPoint;
                        var m2 = sil.GetEndPoint() as IMathPoint;
                        if (m1 != null) s1 = m1.ArrayData as double[];
                        if (m2 != null) s2 = m2.ArrayData as double[];
                    }
                    catch { }
                    if (s1 == null || s2 == null || s1.Length < 3 || s2.Length < 3)
                    {
                        pi.Text = "silhouette-without-endpoints";
                        return pi;
                    }
                    double sx, sy;
                    OrientOnViewPlane(s2[0] - s1[0], s2[1] - s1[1], s2[2] - s1[2], xdir, ydir, out sx, out sy);
                    if (sy > sx) { pi.IsVertical = true; pi.Text = "silhouette VERTICAL"; }
                    else if (sx > sy) { pi.IsHorizontal = true; pi.Text = "silhouette HORIZONTAL"; }
                    else pi.Text = "silhouette oblique";
                    pi.Text += $" (sp={s1[0] * 1000:0.###},{s1[1] * 1000:0.###},{s1[2] * 1000:0.###}" +
                        $" -> {s2[0] * 1000:0.###},{s2[1] * 1000:0.###},{s2[2] * 1000:0.###}" +
                        $" | sx={sx * 1000:0.##} sy={sy * 1000:0.##})";
                    return pi;
                }
                var edge = o as IEdge;
                if (edge == null)
                {
                    pi.Text = "not-an-edge(" + (o == null ? "null" : o.GetType().Name) + ")";
                    return pi;
                }
                var curve = edge.GetCurve() as ICurve;
                if (curve == null) { pi.Text = "edge-without-curve"; return pi; }

                double[] lp = null;
                try { lp = curve.LineParams as double[]; } catch { }
                if (lp != null && lp.Length >= 6)
                {
                    // Orient against the VIEW PLANE, not model space: an edge that runs along a model
                    // axis that this view looks down (e.g. the left boundary of a top view runs along
                    // model Z) has dx = dy = 0 in model space and would never classify as vertical.
                    // ICurve.LineParams is [start point (3 doubles), direction vector (3 doubles)] — NOT
                    // two points: measured live, the direction comes back axis-aligned with magnitude
                    // 1000 (e.g. "0,-1000,0"), and treating the triplets as two points mis-orients every
                    // edge near the view frame (the point coordinates leak into the direction).
                    double sx, sy;
                    OrientOnViewPlane(lp[3], lp[4], lp[5], xdir, ydir, out sx, out sy);
                    if (sy > sx) { pi.IsVertical = true; pi.Text = "line VERTICAL"; }
                    else if (sx > sy) { pi.IsHorizontal = true; pi.Text = "line HORIZONTAL"; }
                    else pi.Text = "line oblique";
                    pi.Text += $" (lp={lp[0] * 1000:0.##},{lp[1] * 1000:0.##},{lp[2] * 1000:0.##}" +
                        $" -> {lp[3] * 1000:0.##},{lp[4] * 1000:0.##},{lp[5] * 1000:0.##} | sx={sx * 1000:0.##} sy={sy * 1000:0.##})";
                    return pi;
                }
                double[] cp = null;
                try { cp = curve.CircleParams as double[]; } catch { }
                if (cp != null && cp.Length >= 7)
                {
                    pi.IsCircle = true;
                    pi.R = cp[6];
                    pi.Text = "circle r=" + (cp[6] * 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + "mm";
                    return pi;
                }
                pi.Text = "other-curve";
                return pi;
            }
            catch (Exception ex) { pi.Text = "describe-error:" + ex.Message; return pi; }
        }

        // Orient a direction against the VIEW plane, never model space alone: a direction that runs along
        // a model axis this view looks down (the left boundary of a top view runs along model Z) is not
        // axis-aligned in the view at all. Without a frame, fall back to the raw components.
        private static void OrientOnViewPlane(double dx, double dy, double dz,
            double[] xdir, double[] ydir, out double sx, out double sy)
        {
            if (xdir != null && ydir != null && xdir.Length >= 3 && ydir.Length >= 3)
            {
                sx = Math.Abs(dx * xdir[0] + dy * xdir[1] + dz * xdir[2]);
                sy = Math.Abs(dx * ydir[0] + dy * ydir[1] + dz * ydir[2]);
            }
            else { sx = Math.Abs(dx); sy = Math.Abs(dy); }
        }

        private bool MatchesExpect(PickInfo info, string expect, double expectR)
        {
            switch (expect)
            {
                case "vline": return info.IsVertical;
                case "hline": return info.IsHorizontal;
                case "vertex": return info.IsVertex || info.IsSketchPoint;
                case "circle":
                    if (!info.IsCircle) return false;
                    if (expectR <= 0) return true;
                    return Math.Abs(info.R - expectR) < 0.0002;   // 0.2 mm
                default: return true;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using SolidworksExecution.Models;

namespace SolidworksExecution.Infrastructure
{
    /// <summary>
    /// Extensible command registry replacing the monolithic 45-branch switch-case statement.
    /// Provides thread-safe, modular registration and dispatch of CAD operations.
    /// </summary>
    public class ToolRegistry
    {
        private readonly Dictionary<string, Func<ToolRequest, ExecutionResponse>> _handlers
            = new Dictionary<string, Func<ToolRequest, ExecutionResponse>>(StringComparer.OrdinalIgnoreCase);

        public void Register(string toolName, Func<ToolRequest, ExecutionResponse> handler)
        {
            if (string.IsNullOrEmpty(toolName)) throw new ArgumentNullException(nameof(toolName));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers[toolName] = handler;
        }

        public void Register(IToolHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Register(handler.ToolName, handler.Handle);
        }

        public ExecutionResponse Dispatch(ToolRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.Tool)) return null;

            Func<ToolRequest, ExecutionResponse> handler;
            if (_handlers.TryGetValue(request.Tool, out handler))
            {
                return handler(request);
            }
            return null;
        }

        public void RegisterDrawing(SolidworksExecution.Services.Interfaces.IDrawingService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            Register("create_drawing", service.CreateDrawing);
            Register("set_drawing_sheet", service.SetDrawingSheet);
            Register("add_drawing_sheet", service.AddDrawingSheet);
            Register("add_drawing_note", service.AddDrawingNote);
            Register("add_balloon", service.AddBalloon);
            Register("auto_balloon", service.AutoBalloon);
            Register("set_document_units", service.SetDocumentUnits);
            Register("add_drawing_view", service.AddDrawingView);
            Register("add_flat_pattern_view", service.AddFlatPatternView);
            Register("add_drawing_dimension", service.AddDrawingDimension);
            Register("delete_drawing_dimension", service.DeleteDrawingDimension);
            Register("auto_dimension_drawing", service.AutoDimensionDrawing);
            Register("auto_center_marks", service.AutoCenterMarks);
            Register("add_hole_callout", service.AddHoleCallout);
            Register("add_section_view", service.AddSectionView);
            Register("analyze_drawing", service.AnalyzeDrawing);
            Register("analyze_slddrw_test", service.AnalyzeDrawing);
        }

        public void RegisterDocument(SolidworksExecution.Services.Interfaces.IDocumentService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            Register("open_document", service.OpenDocument);
            Register("save_document", service.SaveDocument);
            Register("close_document", service.CloseDocument);
            Register("activate_document", service.ActivateDocument);
            Register("export_document", service.ExportDocument);
            Register("batch_export", service.BatchExport);
            Register("verify_state", service.VerifyState);
        }

        public void RegisterPart(SolidworksExecution.Services.Interfaces.IPartService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            Register("open_new_part", service.OpenNewPart);
            Register("extrude_feature", service.ExtrudeFeature);
            Register("create_rib", service.CreateRib);
            Register("add_edge_feature", service.AddEdgeFeature);
            Register("add_reference_geometry", service.AddReferenceGeometry);
            Register("create_pattern", service.CreatePattern);
            Register("set_part_material", service.SetPartMaterial);
            Register("modify_dimension", service.ModifyDimension);
            Register("edit_feature", service.EditFeature);
        }

        public void RegisterSketch(SolidworksExecution.Services.Interfaces.ISketchService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            Register("create_sketch", service.CreateSketch);
            Register("add_sketch_entity", service.AddSketchEntity);
            Register("add_sketch_entities", service.AddSketchEntities);
            Register("add_dimension", service.AddDimension);
            Register("add_sketch_constraint", service.AddSketchConstraint);
            Register("edit_sketch", service.EditSketch);
        }

        public void RegisterAssembly(SolidworksExecution.Services.Interfaces.IAssemblyService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            Register("open_new_assembly", service.OpenNewAssembly);
            Register("insert_component", service.InsertComponent);
            Register("add_mate", service.AddMateTool);
            Register("save_body_as_part", service.SaveBodyAsPart);
            Register("analyze_assembly", service.AnalyzeAssembly);
        }

        public void RegisterSheetMetal(SolidworksExecution.Services.Interfaces.ISheetMetalService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            Register("sheet_metal_feature", service.SheetMetalFeature);
        }

        public void RegisterAnalysis(SolidworksExecution.Services.Interfaces.IAnalysisService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            Register("analyze_model", service.AnalyzeModel);
            Register("get_selection", service.GetSelection);
        }

        public bool Contains(string toolName)
        {
            return !string.IsNullOrEmpty(toolName) && _handlers.ContainsKey(toolName);
        }
    }
}

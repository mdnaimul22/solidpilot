using SolidworksExecution.Models;

namespace SolidworksExecution.Services.Interfaces
{
    public interface IDrawingService
    {
        ExecutionResponse CreateDrawing(ToolRequest request);
        ExecutionResponse AddDrawingView(ToolRequest request);
        ExecutionResponse AddFlatPatternView(ToolRequest request);
        ExecutionResponse AddDrawingDimension(ToolRequest request);
        ExecutionResponse DeleteDrawingDimension(ToolRequest request);
        ExecutionResponse AutoDimensionDrawing(ToolRequest request);
        ExecutionResponse AutoCenterMarks(ToolRequest request);
        ExecutionResponse AddHoleCallout(ToolRequest request);
        ExecutionResponse AddSectionView(ToolRequest request);
        ExecutionResponse SetDrawingSheet(ToolRequest request);
        ExecutionResponse AddDrawingSheet(ToolRequest request);
        ExecutionResponse AddDrawingNote(ToolRequest request);
        ExecutionResponse AddBalloon(ToolRequest request);
        ExecutionResponse AutoBalloon(ToolRequest request);
        ExecutionResponse SetDocumentUnits(ToolRequest request);
        ExecutionResponse AnalyzeDrawing(ToolRequest request);
    }
}

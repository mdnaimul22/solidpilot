using SolidworksExecution.Models;

namespace SolidworksExecution.Services.Interfaces
{
    public interface IPartService
    {
        ExecutionResponse OpenNewPart(ToolRequest request);
        ExecutionResponse ExtrudeFeature(ToolRequest request);
        ExecutionResponse CreateRib(ToolRequest request);
        ExecutionResponse AddEdgeFeature(ToolRequest request);
        ExecutionResponse AddReferenceGeometry(ToolRequest request);
        ExecutionResponse CreatePattern(ToolRequest request);
        ExecutionResponse SetPartMaterial(ToolRequest request);
        ExecutionResponse ModifyDimension(ToolRequest request);
        ExecutionResponse EditFeature(ToolRequest request);
    }
}

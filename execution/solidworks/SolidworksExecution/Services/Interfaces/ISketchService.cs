using SolidworksExecution.Models;

namespace SolidworksExecution.Services.Interfaces
{
    public interface ISketchService
    {
        ExecutionResponse CreateSketch(ToolRequest request);
        ExecutionResponse AddSketchEntity(ToolRequest request);
        ExecutionResponse AddSketchEntities(ToolRequest request);
        ExecutionResponse AddDimension(ToolRequest request);
        ExecutionResponse AddSketchConstraint(ToolRequest request);
        ExecutionResponse EditSketch(ToolRequest request);
    }
}

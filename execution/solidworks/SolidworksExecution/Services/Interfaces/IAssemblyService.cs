using SolidworksExecution.Models;

namespace SolidworksExecution.Services.Interfaces
{
    public interface IAssemblyService
    {
        ExecutionResponse OpenNewAssembly(ToolRequest request);
        ExecutionResponse InsertComponent(ToolRequest request);
        ExecutionResponse AddMateTool(ToolRequest request);
        ExecutionResponse SaveBodyAsPart(ToolRequest request);
        ExecutionResponse AnalyzeAssembly(ToolRequest request);
    }
}

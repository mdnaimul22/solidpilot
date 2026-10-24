using SolidworksExecution.Models;

namespace SolidworksExecution.Services.Interfaces
{
    public interface IAnalysisService
    {
        ExecutionResponse AnalyzeModel(ToolRequest request);
        ExecutionResponse GetSelection(ToolRequest request);
    }
}

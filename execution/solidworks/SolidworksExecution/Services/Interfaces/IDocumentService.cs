using SolidworksExecution.Models;

namespace SolidworksExecution.Services.Interfaces
{
    public interface IDocumentService
    {
        ExecutionResponse OpenDocument(ToolRequest request);
        ExecutionResponse SaveDocument(ToolRequest request);
        ExecutionResponse CloseDocument(ToolRequest request);
        ExecutionResponse ActivateDocument(ToolRequest request);
        ExecutionResponse ExportDocument(ToolRequest request);
        ExecutionResponse BatchExport(ToolRequest request);
        ExecutionResponse VerifyState(ToolRequest request);
    }
}

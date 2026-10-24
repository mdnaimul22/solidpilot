using SolidworksExecution.Models;

namespace SolidworksExecution.Services.Interfaces
{
    public interface ISheetMetalService
    {
        ExecutionResponse SheetMetalFeature(ToolRequest request);
    }
}

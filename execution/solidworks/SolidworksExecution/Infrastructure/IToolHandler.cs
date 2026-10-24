using SolidworksExecution.Models;

namespace SolidworksExecution.Infrastructure
{
    /// <summary>
    /// Contract for a modular tool handler. Decouples ToolController from the monolithic service switch-case.
    /// </summary>
    public interface IToolHandler
    {
        string ToolName { get; }
        ExecutionResponse Handle(ToolRequest request);
    }
}

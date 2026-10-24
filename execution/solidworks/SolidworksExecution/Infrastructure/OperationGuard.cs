using System.Collections.Concurrent;
using System.Threading;
using SolidworksExecution.Models;

namespace SolidworksExecution.Infrastructure
{
    public class OperationGuard : IOperationGuard
    {
        private static readonly OperationGuard _instance = new OperationGuard();
        public static OperationGuard Instance => _instance;

        private const int MaxTrackedOperations = 5000;
        private readonly ConcurrentDictionary<string, ExecutionResponse> _completed
            = new ConcurrentDictionary<string, ExecutionResponse>();
        private readonly ConcurrentQueue<string> _operationOrder = new ConcurrentQueue<string>();

        private volatile int _currentStateVersion = 0;

        private OperationGuard() { }

        public bool IsDuplicate(string operationId)
        {
            return _completed.ContainsKey(operationId);
        }

        public ExecutionResponse GetDuplicate(string operationId)
        {
            if (_completed.TryGetValue(operationId, out var original))
            {
                return new ExecutionResponse
                {
                    OperationId = operationId,
                    Status = "DUPLICATE",
                    LastKnownStateVersion = _currentStateVersion,
                    CadState = null,
                    Error = null
                };
            }
            return null;
        }

        public bool IsStateVersionValid(int incomingStateVersion)
        {
            return incomingStateVersion == _currentStateVersion;
        }

        public void RegisterCompleted(string operationId, ExecutionResponse response, bool incrementsState = true)
        {
            _completed[operationId] = response;
            _operationOrder.Enqueue(operationId);

            // Bounded memory protection for 30,000+ files: evict oldest entries beyond capacity
            while (_operationOrder.Count > MaxTrackedOperations && _operationOrder.TryDequeue(out var oldId))
            {
                _completed.TryRemove(oldId, out _);
            }

            if (incrementsState)
            {
                Interlocked.Increment(ref _currentStateVersion);
            }
        }

        public int GetCurrentStateVersion()
        {
            return _currentStateVersion;
        }
    }
}

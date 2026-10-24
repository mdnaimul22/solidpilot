using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

namespace SolidworksExecution.Infrastructure
{
    /// <summary>
    /// Monitors SolidWorks process health during massive batch operations.
    /// Recycles the SolidWorks process cleanly every N documents (default: 100)
    /// or when unmanaged memory exceeds 2.5 GB to prevent GDI / heap exhaustion.
    /// </summary>
    public class ProcessRecycler
    {
        private static readonly ProcessRecycler _instance = new ProcessRecycler();
        public static ProcessRecycler Instance => _instance;

        private int _docsProcessedCount = 0;
        private const int MaxDocsBeforeRecycle = 100;
        private const long MaxMemoryBytes = 2500L * 1024 * 1024; // 2.5 GB

        private ProcessRecycler() { }

        public void NotifyDocumentProcessed()
        {
            System.Threading.Interlocked.Increment(ref _docsProcessedCount);
        }

        public bool ShouldRecycle(ISldWorks sw, out string reason)
        {
            reason = null;
            if (_docsProcessedCount >= MaxDocsBeforeRecycle)
            {
                reason = $"Document threshold reached ({_docsProcessedCount} >= {MaxDocsBeforeRecycle})";
                return true;
            }

            try
            {
                var procs = Process.GetProcessesByName("SLDWORKS");
                foreach (var p in procs)
                {
                    if (p.WorkingSet64 > MaxMemoryBytes)
                    {
                        reason = $"Memory limit exceeded (PID {p.Id} using {p.WorkingSet64 / (1024 * 1024)} MB > {MaxMemoryBytes / (1024 * 1024)} MB)";
                        return true;
                    }
                }
            }
            catch
            {
                // Process inspection diagnostic only
            }

            return false;
        }

        public bool Recycle(ref ISldWorks sw)
        {
            ExecLog.Write($"[ProcessRecycler] Recycling SolidWorks process (docs processed={_docsProcessedCount})...");

            try
            {
                if (sw != null)
                {
                    try { sw.CloseAllDocuments(true); } catch { }
                    try { sw.ExitApp(); } catch { }
                    ComScope.Release(sw);
                    sw = null;
                }
            }
            catch (Exception ex)
            {
                ExecLog.Write($"[ProcessRecycler] Error during ExitApp: {ex.Message}");
            }

            // Force garbage collection on STA thread
            ComScope.CollectGarbage();

            // Wait up to 10 seconds for any existing SLDWORKS to terminate
            var swProcesses = Process.GetProcessesByName("SLDWORKS");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && Process.GetProcessesByName("SLDWORKS").Length > 0)
            {
                System.Threading.Thread.Sleep(500);
            }

            // If still alive, force kill
            foreach (var p in Process.GetProcessesByName("SLDWORKS"))
            {
                try
                {
                    ExecLog.Write($"[ProcessRecycler] Force-killing lingering SolidWorks PID {p.Id}");
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch { }
            }

            _docsProcessedCount = 0;
            ExecLog.Write("[ProcessRecycler] SolidWorks process recycled successfully.");
            return true;
        }
    }
}

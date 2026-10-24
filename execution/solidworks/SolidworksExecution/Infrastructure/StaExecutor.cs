using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SolidworksExecution.Infrastructure
{
    /// <summary>
    /// Runs delegates on a single, long-lived, dedicated STA thread. (P0.1)
    ///
    /// SolidWorks COM objects must be called from an STA apartment. OWIN / Web API request
    /// threads are MTA, and while COM marshaling lets lightweight calls (e.g. FeatureExtrusion3)
    /// limp across the apartment boundary, heavier or more apartment-sensitive calls fail off-STA:
    /// InsertSheetMetalBaseFlange2 silently returned null and add_edge_feature (edge fillet)
    /// deadlocked (ContextSwitchDeadlock). Routing ALL COM-touching work through one STA thread —
    /// including the GetActiveObject attach — makes the RCW live on the STA, fixing both.
    ///
    /// A single worker thread also serializes COM access, which suits the single-writer,
    /// process-global state model the OperationGuard already assumes.
    /// </summary>
    public sealed class StaExecutor
    {
        public static readonly StaExecutor Instance = new StaExecutor();

        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();

        private volatile int _staThreadId = -1;

        private StaExecutor()
        {
            var thread = new Thread(Worker)
            {
                IsBackground = true,
                Name = "SolidWorksStaThread"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        public static void PumpMessages()
        {
            MSG msg;
            while (PeekMessage(out msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        private void Worker()
        {
            _staThreadId = Thread.CurrentThread.ManagedThreadId;
            MessageFilter.Register();
            try
            {
                // STA worker loop with continuous Windows message pumping (avoids ContextSwitchDeadlock)
                while (!_queue.IsCompleted)
                {
                    Action work;
                    if (_queue.TryTake(out work, 50))
                    {
                        work();
                        PumpMessages();
                    }
                    else
                    {
                        PumpMessages();
                    }
                }
            }
            finally
            {
                MessageFilter.Revoke();
            }
        }

        /// <summary>Marshal <paramref name="func"/> onto the STA thread non-blockingly via Task.</summary>
        public System.Threading.Tasks.Task<T> RunAsync<T>(Func<T> func)
        {
            if (Thread.CurrentThread.ManagedThreadId == _staThreadId)
            {
                try { return System.Threading.Tasks.Task.FromResult(func()); }
                catch (Exception ex)
                {
                    var failTcs = new System.Threading.Tasks.TaskCompletionSource<T>();
                    failTcs.SetException(ex);
                    return failTcs.Task;
                }
            }

            var tcs = new System.Threading.Tasks.TaskCompletionSource<T>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        /// <summary>Marshal <paramref name="func"/> onto the STA thread and block until it returns.</summary>
        public T Run<T>(Func<T> func)
        {
            if (Thread.CurrentThread.ManagedThreadId == _staThreadId)
            {
                // Re-entrant call on the STA thread: execute directly to prevent deadlock.
                return func();
            }

            T result = default(T);
            Exception captured = null;
            using (var done = new ManualResetEventSlim(false))
            {
                _queue.Add(() =>
                {
                    try { result = func(); }
                    catch (Exception ex) { captured = ex; }
                    finally { done.Set(); }
                });
                done.Wait();
            }
            if (captured != null)
                throw new InvalidOperationException("STA execution failed: " + captured.Message, captured);
            return result;
        }
    }
}

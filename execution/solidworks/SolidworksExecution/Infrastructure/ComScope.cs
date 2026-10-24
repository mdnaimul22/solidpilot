using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SolidworksExecution.Infrastructure
{
    /// <summary>
    /// Tracks COM objects created during an operation and releases them deterministically on Dispose.
    /// Essential for batch processing thousands of SolidWorks documents without unmanaged heap exhaustion.
    /// </summary>
    public sealed class ComScope : IDisposable
    {
        private readonly List<object> _tracked = new List<object>();
        private bool _disposed;

        public T Track<T>(T comObject) where T : class
        {
            if (comObject != null && Marshal.IsComObject(comObject))
            {
                _tracked.Add(comObject);
            }
            return comObject;
        }

        public static void Release(object comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject))
            {
                try
                {
                    Marshal.FinalReleaseComObject(comObject);
                }
                catch
                {
                    // Ignore disconnect or already released errors
                }
            }
        }

        public static void SafeRelease<T>(ref T comObject) where T : class
        {
            if (comObject != null)
            {
                Release(comObject);
                comObject = null;
            }
        }

        public static void ReleaseAll(System.Collections.IEnumerable comObjects)
        {
            if (comObjects == null) return;
            foreach (var item in comObjects)
            {
                Release(item);
            }
        }

        public static void CollectGarbage()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                Release(_tracked[i]);
            }
            _tracked.Clear();
        }
    }
}

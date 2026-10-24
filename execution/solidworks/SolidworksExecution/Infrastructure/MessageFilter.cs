using System;
using System.Runtime.InteropServices;

namespace SolidworksExecution.Infrastructure
{
    /// <summary>
    /// Implements IOleMessageFilter to handle SolidWorks COM busy states (RPC_E_CALL_REJECTED)
    /// and prevent modal dialog boxes (missing fonts, rebuild warnings, old versions) from hanging batch jobs.
    /// </summary>
    public class MessageFilter : IOleMessageFilter
    {
        private const int SERVERCALL_ISHANDLED = 0;
        private const int SERVERCALL_RETRYLATER = 2;
        private const int PENDINGMSG_WAITDEFPROCESS = 2;

        public static void Register()
        {
            IOleMessageFilter newFilter = new MessageFilter();
            IOleMessageFilter oldFilter;
            CoRegisterMessageFilter(newFilter, out oldFilter);
        }

        public static void Revoke()
        {
            IOleMessageFilter oldFilter;
            CoRegisterMessageFilter(null, out oldFilter);
        }

        int IOleMessageFilter.HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
        {
            return SERVERCALL_ISHANDLED;
        }

        private const int MaxRetryTimeoutMs = 30000; // 30 seconds max wait for busy/modal dialogs

        int IOleMessageFilter.RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
        {
            if (dwRejectType == SERVERCALL_RETRYLATER)
            {
                if (dwTickCount > MaxRetryTimeoutMs)
                {
                    ExecLog.Write($"[MessageFilter] Call rejected by SolidWorks for > {MaxRetryTimeoutMs}ms (dwTickCount={dwTickCount}). Aborting call.");
                    return -1;
                }
                // Retry in 150ms if SolidWorks is temporarily busy
                return 150;
            }
            // Cancel call if completely rejected
            return -1;
        }

        int IOleMessageFilter.MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType)
        {
            // Continue waiting for the call to complete while dispatching necessary Windows messages
            return PENDINGMSG_WAITDEFPROCESS;
        }

        [DllImport("Ole32.dll")]
        private static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);
    }

    [ComImport]
    [Guid("00000016-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleMessageFilter
    {
        [PreserveSig]
        int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);

        [PreserveSig]
        int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);

        [PreserveSig]
        int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }
}

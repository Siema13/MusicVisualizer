using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MusicVisualizerLoopback
{
    internal sealed class HighResWaitTimer : IDisposable
    {
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x1F0003;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(
            IntPtr lpTimerAttributes,
            string? lpTimerName,
            uint dwFlags,
            uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimerEx(
            IntPtr hTimer,
            ref long lpDueTime,
            int lPeriod,
            IntPtr pfnCompletionRoutine,
            IntPtr lpArgToCompletionRoutine,
            IntPtr wakeContext,
            uint tolerableDelay);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForMultipleObjects(
            uint nCount,
            IntPtr[] lpHandles,
            bool bWaitAll,
            uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private IntPtr _handle;
        private bool _disposed;
        private readonly IntPtr[] _waitHandles = new IntPtr[3];

        private HighResWaitTimer(IntPtr handle)
        {
            _handle = handle;
            _waitHandles[0] = handle;
        }

        public static HighResWaitTimer? TryCreate()
        {
            try
            {
                IntPtr handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
                if (handle != IntPtr.Zero)
                {
                    return new HighResWaitTimer(handle);
                }
            }
            catch
            {
            }

            return null;
        }

        public void Wait(long delay100ns, CancellationToken cancellationToken, WaitHandle? wakeHandle = null)
        {
            if (_disposed || _handle == IntPtr.Zero || delay100ns <= 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            long dueTime = -delay100ns;
            if (!SetWaitableTimerEx(_handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            {
                int fallbackMs = Math.Max(1, (int)(delay100ns / 10_000));
                cancellationToken.WaitHandle.WaitOne(fallbackMs);
                return;
            }

            var cancelWaitHandle = cancellationToken.WaitHandle.SafeWaitHandle;
            var wakeSafeHandle = wakeHandle?.SafeWaitHandle;
            bool addedCancelRef = false;
            bool addedWakeRef = false;
            try
            {
                cancelWaitHandle.DangerousAddRef(ref addedCancelRef);
                _waitHandles[1] = cancelWaitHandle.DangerousGetHandle();
                uint count = 2;
                if (wakeSafeHandle != null && !wakeSafeHandle.IsClosed && !wakeSafeHandle.IsInvalid)
                {
                    wakeSafeHandle.DangerousAddRef(ref addedWakeRef);
                    _waitHandles[2] = wakeSafeHandle.DangerousGetHandle();
                    count = 3;
                }

                uint res = WaitForMultipleObjects(count, _waitHandles, false, uint.MaxValue);
                if (res == 0xFFFFFFFFu)
                {
                    int fallbackMs = Math.Max(1, (int)(delay100ns / 10_000));
                    cancellationToken.WaitHandle.WaitOne(fallbackMs);
                }
            }
            finally
            {
                if (addedWakeRef && wakeSafeHandle != null)
                {
                    wakeSafeHandle.DangerousRelease();
                }

                if (addedCancelRef)
                {
                    cancelWaitHandle.DangerousRelease();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}

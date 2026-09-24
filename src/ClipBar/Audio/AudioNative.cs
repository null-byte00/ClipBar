using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClipBar.Audio;

internal static class AudioNative
{
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimer(SafeWaitHandle timer, in long dueTime, int period, IntPtr routine, IntPtr arg, bool resume);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    const uint CreateWaitableTimerHighResolution = 0x0002;
    const uint TimerAllAccess = 0x1F0003;

    public static IntPtr EnterProAudio()
    {
        uint index = 0;
        try { return AvSetMmThreadCharacteristicsW("Pro Audio", ref index); }
        catch { return IntPtr.Zero; }
    }

    public static void LeaveMmcss(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { AvRevertMmThreadCharacteristics(handle); } catch { }
    }

    public static WaitHandle? CreatePeriodicTimer(int periodMs)
    {
        try
        {
            var h = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (h.IsInvalid)
            {
                h.Dispose();
                h = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
            }
            if (h.IsInvalid) return null;
            long due = -periodMs * 10_000L;
            if (!SetWaitableTimer(h, in due, periodMs, IntPtr.Zero, IntPtr.Zero, false))
            {
                h.Dispose();
                return null;
            }
            return new TimerWaitHandle(h);
        }
        catch
        {
            return null;
        }
    }

    sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }
}

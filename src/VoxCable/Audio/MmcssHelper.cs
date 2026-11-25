using System.Runtime.InteropServices;

namespace VoxCable.Audio;

/// <summary>
/// Manages Windows Multimedia Class Scheduler Service (MMCSS) "Pro Audio" boost
/// and 1 ms multimedia timer resolution while an active low-latency bridge is running.
/// </summary>
public static class MmcssHelper
{
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint uPeriod);

    public static IDisposable EnterProAudioThread()
    {
        try
        {
            uint taskIndex = 0;
            IntPtr handle = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
            return handle != IntPtr.Zero ? new AvrtScope(handle) : NullScope.Instance;
        }
        catch
        {
            return NullScope.Instance;
        }
    }

    public static IDisposable RequestHighResTimer(uint periodMs = 1)
    {
        try
        {
            if (timeBeginPeriod(periodMs) == 0)
            {
                return new TimerPeriodScope(periodMs);
            }
        }
        catch
        {
            // Ignore on restricted environments
        }

        return NullScope.Instance;
    }

    private sealed class AvrtScope : IDisposable
    {
        private IntPtr _handle;
        public AvrtScope(IntPtr handle) => _handle = handle;
        public void Dispose()
        {
            IntPtr h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (h != IntPtr.Zero)
            {
                try { AvRevertMmThreadCharacteristics(h); } catch { }
            }
        }
    }

    private sealed class TimerPeriodScope : IDisposable
    {
        private readonly uint _period;
        private int _disposed;
        public TimerPeriodScope(uint period) => _period = period;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { timeEndPeriod(_period); } catch { }
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

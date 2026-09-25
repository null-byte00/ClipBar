using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipBar.Core;

namespace ClipBar.UI.Overlay.Widgets;

public sealed record PerfSample(
    double CpuPercent,
    double? GpuPercent,
    double RamUsedGb,
    double RamTotalGb,
    double AppCpuPercent,
    double AppMemoryMb);

public sealed partial class PerformanceSampler : IDisposable
{
    public event Action<PerfSample>? Sampled;

    Timer? _timer;
    int _busy;

    long _prevIdle, _prevKernel, _prevUser;
    readonly int _pid = Environment.ProcessId;
    readonly Dictionary<int, (Process Proc, TimeSpan Cpu)> _tracked = new();
    DateTime _prevWall;
    IntPtr _pdhQuery, _pdhCounter;
    bool _gpuUnavailable;
    int _gpuAgeTicks;

    public void Start()
    {
        if (_timer is not null) return;
        _prevWall = DateTime.UtcNow;
        try { SampleCpu(); } catch { }
        try { SampleApp(); } catch { }
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        ClosePdh();
        // Освобождаем хендлы отслеживаемых процессов, иначе копятся при каждом открытии/закрытии панели.
        foreach (var (p, _) in _tracked.Values) { try { p.Dispose(); } catch { } }
        _tracked.Clear();
    }

    public void Dispose() => Stop();

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var cpu = SafeCall(SampleCpu, 0.0);
            var gpu = _gpuUnavailable ? null : SafeCall(SampleGpu, (double?)null);
            var (used, total) = SafeCall(SampleRam, (0.0, 0.0));
            var (appCpu, appMem) = SafeCall(SampleApp, (0.0, 0.0));
            Sampled?.Invoke(new PerfSample(cpu, gpu, used, total, appCpu, appMem));
        }
        catch (Exception ex) { Log.Error("Performance sample failed", ex); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    static T SafeCall<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }

    double SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var dIdle = idle - _prevIdle; var dKernel = kernel - _prevKernel; var dUser = user - _prevUser;
        _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
        var total = dKernel + dUser;
        if (total <= 0) return 0;
        return Math.Clamp(100.0 * (total - dIdle) / total, 0, 100);
    }

    static (double UsedGb, double TotalGb) SampleRam()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref ms)) return (0, 0);
        const double gb = 1024.0 * 1024 * 1024;
        return ((ms.ullTotalPhys - ms.ullAvailPhys) / gb, ms.ullTotalPhys / gb);
    }

    (double CpuPercent, double MemoryMb) SampleApp()
    {
        var now = DateTime.UtcNow;
        var wall = (now - _prevWall).TotalSeconds;
        _prevWall = now;

        var live = new HashSet<int> { _pid };
        foreach (var child in FindChildFfmpeg()) live.Add(child);

        foreach (var pid in live)
        {
            if (_tracked.ContainsKey(pid)) continue;
            try
            {
                var p = Process.GetProcessById(pid);
                _tracked[pid] = (p, p.TotalProcessorTime);
            }
            catch { }
        }

        double cpuSeconds = 0, memBytes = 0;
        foreach (var pid in _tracked.Keys.ToArray())
        {
            var (proc, prevCpu) = _tracked[pid];
            if (!live.Contains(pid))
            {
                proc.Dispose();
                _tracked.Remove(pid);
                continue;
            }
            try
            {
                proc.Refresh();
                if (proc.HasExited) { proc.Dispose(); _tracked.Remove(pid); continue; }
                var cpu = proc.TotalProcessorTime;
                cpuSeconds += Math.Max(0, (cpu - prevCpu).TotalSeconds);
                memBytes += proc.WorkingSet64;
                _tracked[pid] = (proc, cpu);
            }
            catch { proc.Dispose(); _tracked.Remove(pid); }
        }

        var percent = wall > 0 ? 100.0 * cpuSeconds / wall / Environment.ProcessorCount : 0;
        return (Math.Clamp(percent, 0, 100), memBytes / (1024.0 * 1024));
    }

    IEnumerable<int> FindChildFfmpeg()
    {
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) yield break;
        try
        {
            var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref pe)) yield break;
            do
            {
                if (pe.th32ParentProcessID == (uint)_pid &&
                    pe.szExeFile.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase))
                    yield return (int)pe.th32ProcessID;
            } while (Process32NextW(snap, ref pe));
        }
        finally { CloseHandle(snap); }
    }

    double? SampleGpu()
    {
        if (_pdhQuery != IntPtr.Zero && ++_gpuAgeTicks >= 30) ClosePdh();

        if (_pdhQuery == IntPtr.Zero)
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _pdhQuery) != 0) { _gpuUnavailable = true; return null; }
            if (PdhAddEnglishCounterW(_pdhQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _pdhCounter) != 0)
            {
                ClosePdh(); _gpuUnavailable = true; return null;
            }
            _gpuAgeTicks = 0;
            PdhCollectQueryData(_pdhQuery);
            return null;
        }

        if (PdhCollectQueryData(_pdhQuery) != 0) return null;

        uint bufSize = 0, count = 0;
        var status = PdhGetFormattedCounterArrayW(_pdhCounter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bufSize, ref count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || bufSize == 0) return null;

        var buf = Marshal.AllocHGlobal((int)bufSize);
        try
        {
            status = PdhGetFormattedCounterArrayW(_pdhCounter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bufSize, ref count, buf);
            if (status != 0) return null;
            double sum = 0;
            var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(buf + i * itemSize);
                if (item.FmtValue.CStatus != 0) continue;
                var name = Marshal.PtrToStringUni(item.szName);
                if (name is null || !name.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                sum += item.FmtValue.doubleValue;
            }
            return Math.Clamp(sum, 0, 100);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    void ClosePdh()
    {
        if (_pdhQuery != IntPtr.Zero) { try { PdhCloseQuery(_pdhQuery); } catch { } }
        _pdhQuery = IntPtr.Zero; _pdhCounter = IntPtr.Zero;
    }

    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    const uint TH32CS_SNAPPROCESS = 0x2;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W entry);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(IntPtr h);

    const uint PDH_FMT_DOUBLE = 0x00000200;
    const uint PDH_FMT_NOCAP100 = 0x00008000;
    const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
}

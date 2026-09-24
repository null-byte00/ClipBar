using System.Runtime.InteropServices;
using ClipBar.Core;
using Microsoft.Win32;

namespace ClipBar.UI.Pages;

internal static class SettingsNative
{
    public sealed record MonitorInfo(int Index, int Width, int Height, bool IsPrimary, string DeviceName);

    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    list.Add(new MonitorInfo(list.Count,
                        info.rcMonitor.Right - info.rcMonitor.Left,
                        info.rcMonitor.Bottom - info.rcMonitor.Top,
                        (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                        info.szDevice ?? ""));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn("EnumDisplayMonitors failed: " + ex.Message);
        }
        list.Sort((a, b) => string.CompareOrdinal(a.DeviceName, b.DeviceName));
        for (var i = 0; i < list.Count; i++) list[i] = list[i] with { Index = i };
        return list;
    }

    public static List<string> GetGpuNames()
    {
        var names = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return names;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string desc && !string.IsNullOrWhiteSpace(desc) && !names.Contains(desc))
                {
                    if (desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || desc.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
                    names.Add(desc.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("GPU registry lookup failed: " + ex.Message);
        }
        return names;
    }

    const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
}

using System.Runtime.InteropServices;

namespace ClipBar.UI.Overlay;

internal static partial class OverlayNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_APPWINDOW = 0x00040000;

    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    const uint MONITOR_DEFAULTTONEAREST = 2;

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out POINT pt);
    [LibraryImport("user32.dll")] private static partial IntPtr MonitorFromPoint(POINT pt, uint flags);
    [LibraryImport("user32.dll")] private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [LibraryImport("shcore.dll")] private static partial int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")] public static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(IntPtr hWnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindow(IntPtr hWnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool BringWindowToTop(IntPtr hWnd);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
    [LibraryImport("kernel32.dll")] public static partial uint GetCurrentThreadId();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")] private static partial int GetWindowLong32(IntPtr hWnd, int nIndex);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")] private static partial int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static partial IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static partial IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, index) : new IntPtr(GetWindowLong32(hWnd, index));

    public static void SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, index, value);
        else SetWindowLong32(hWnd, index, value.ToInt32());
    }

    public static void MakeToolWindow(IntPtr hWnd, bool noActivate = false)
    {
        var ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~(long)WS_EX_APPWINDOW;
        if (noActivate) ex |= WS_EX_NOACTIVATE;
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    public static (RECT Bounds, double Scale) GetMonitorUnderCursor()
    {
        if (!GetCursorPos(out var pt)) pt = default;
        return DescribeMonitor(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST));
    }

    public static (RECT Bounds, double Scale) GetMonitorOfWindow(IntPtr hWnd) =>
        DescribeMonitor(MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST));

    static (RECT Bounds, double Scale) DescribeMonitor(IntPtr hMon)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hMon == IntPtr.Zero || !GetMonitorInfo(hMon, ref mi))
        {
            var w = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
            var h = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
            return (new RECT { Left = 0, Top = 0, Right = w, Bottom = h }, 1.0);
        }
        double scale = 1.0;
        try
        {
            if (GetDpiForMonitor(hMon, 0, out var dx, out _) == 0 && dx > 0)
                scale = dx / 96.0;
        }
        catch { }
        return (mi.rcMonitor, scale);
    }

    public static void ForceForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        if (SetForegroundWindow(hWnd) && GetForegroundWindow() == hWnd) return;

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == hWnd) return;
        var fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
        var me = GetCurrentThreadId();
        if (fgThread == me) { SetForegroundWindow(hWnd); return; }
        var attached = AttachThreadInput(me, fgThread, true);
        try
        {
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, false);
        }
    }
}

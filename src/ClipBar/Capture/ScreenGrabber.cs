using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipBar.Capture;

internal static class ScreenGrabber
{
    public readonly record struct MonitorBounds(int X, int Y, int Width, int Height, bool Primary);

    public static List<MonitorBounds> GetMonitors()
    {
        var list = new List<MonitorBounds>();
        var old = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMon, ref mi))
                    list.Add(new MonitorBounds(mi.rcMonitor.Left, mi.rcMonitor.Top,
                        mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                        (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
                return true;
            }, IntPtr.Zero);
        }
        finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }

        if (list.Count == 0)
            list.Add(new MonitorBounds(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN), true));
        return list;
    }

    public static MonitorBounds GetMonitor(int index)
    {
        var mons = GetMonitors();
        if (index >= 0 && index < mons.Count) return mons[index];
        return mons.Find(m => m.Primary) is { Width: > 0 } p ? p : mons[0];
    }

    public static void SavePng(int monitorIndex, string path)
    {
        var old = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            var m = GetMonitor(monitorIndex);
            if (m.Width <= 0 || m.Height <= 0) throw new InvalidOperationException("Не удалось определить размер экрана");

            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC failed");
            var memDc = IntPtr.Zero; var dib = IntPtr.Zero; var oldBmp = IntPtr.Zero;
            try
            {
                memDc = CreateCompatibleDC(screenDc);
                var bmi = new BITMAPINFO
                {
                    biSize = 40, biWidth = m.Width, biHeight = -m.Height,
                    biPlanes = 1, biBitCount = 32, biCompression = 0,
                };
                dib = CreateDIBSection(memDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
                oldBmp = SelectObject(memDc, dib);
                if (!BitBlt(memDc, 0, 0, m.Width, m.Height, screenDc, m.X, m.Y, SRCCOPY | CAPTUREBLT))
                    throw new InvalidOperationException("BitBlt failed: " + Marshal.GetLastWin32Error());
                GdiFlush();

                var stride = m.Width * 4;
                var source = BitmapSource.Create(m.Width, m.Height, 96, 96, PixelFormats.Bgr32, null, bits, stride * m.Height, stride);
                var enc = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
                enc.Frames.Add(BitmapFrame.Create(source));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".part";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    enc.Save(fs);
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
    }

    public static BitmapSource CaptureBitmap(int x, int y, int width, int height)
    {
        var old = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = IntPtr.Zero; var dib = IntPtr.Zero; var oldBmp = IntPtr.Zero;
        try
        {
            if (screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC failed");
            memDc = CreateCompatibleDC(screenDc);
            var bmi = new BITMAPINFO { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            dib = CreateDIBSection(memDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
            oldBmp = SelectObject(memDc, dib);
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("BitBlt failed: " + Marshal.GetLastWin32Error());
            GdiFlush();
            var stride = width * 4;
            var src = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bits, stride * height, stride);
            src.Freeze();
            return src;
        }
        finally
        {
            if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old);
        }
    }

    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000, DIB_RGB_COLORS = 0, MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public uint[] bmiColors;
    }
    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
}

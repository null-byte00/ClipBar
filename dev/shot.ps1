param(
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$ProcessId = 0,
    [string]$Title = ""
)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ShotNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    public static IntPtr Find(uint pid, string title) {
        IntPtr found = IntPtr.Zero; int bestArea = 0;
        EnumWindows((h, p) => {
            if (!IsWindowVisible(h)) return true;
            uint wp; GetWindowThreadProcessId(h, out wp);
            var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
            bool ok = pid != 0 ? wp == pid : (title.Length > 0 && sb.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0);
            if (ok) {
                RECT r; DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT)));
                int area = (r.R - r.L) * (r.B - r.T);
                if (area > bestArea) { bestArea = area; found = h; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@
[ShotNative]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
$dir = Split-Path -Parent $Out
if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }

if ($ProcessId -ne 0 -or $Title -ne "") {
    $h = [ShotNative]::Find([uint32]$ProcessId, $Title)
    if ($h -eq [IntPtr]::Zero) { Write-Error "window not found"; exit 1 }
    $r = New-Object ShotNative+RECT
    [ShotNative]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
    $w = $r.R - $r.L; $hgt = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $hgt
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $hgt))
    $g.Dispose()
} else {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds 2>$null
    if (-not $b) { Add-Type -AssemblyName System.Windows.Forms; $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds }
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, $b.Size)
    $g.Dispose()
}
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"saved $Out"

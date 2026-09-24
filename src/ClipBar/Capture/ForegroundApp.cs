using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipBar.Capture;

internal static class ForegroundApp
{
    public const string Desktop = "Рабочий стол";

    public static string GetName() => GetForegroundWindowAndName().Name;

    public static string GetLabel()
    {
        var (hwnd, name) = GetForegroundWindowAndName();
        if (name == Desktop) return Desktop;
        var title = hwnd == IntPtr.Zero ? "" : Sanitize(WindowTitle(hwnd), maxLength: 80);
        if (title.Length == 0) return name;
        if (title.Contains(name, StringComparison.OrdinalIgnoreCase)) return title;
        return $"{title} - {name}";
    }

    static (IntPtr Hwnd, string Name) GetForegroundWindowAndName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            var name = NameForWindow(hwnd);
            if (name is not null) return (hwnd, name);

            IntPtr foundHwnd = IntPtr.Zero;
            string? found = null;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || IsIconic(h) || IsCloaked(h)) return true;
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                if (GetWindowTextLength(h) == 0) return true;
                var n = NameForWindow(h);
                if (n is null) return true;
                found = n;
                foundHwnd = h;
                return false;
            }, IntPtr.Zero);
            return (foundHwnd, found ?? Desktop);
        }
        catch { return (IntPtr.Zero, Desktop); }
    }

    static string? NameForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return Desktop;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return Desktop;
        if (pid == Environment.ProcessId) return null;

        var exe = GetProcessPath(pid);
        var procName = exe is null ? "" : Path.GetFileNameWithoutExtension(exe);

        if (ShellProcesses.Contains(procName)) return Desktop;
        if (string.Equals(procName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            var title = WindowTitle(hwnd);
            return title.Length > 0 ? Sanitize(title) : Desktop;
        }

        string? desc = null;
        if (exe is not null)
        {
            try { desc = FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { }
        }
        desc = desc?.Trim();
        if (string.IsNullOrEmpty(desc) || desc.Contains("Microsoft® Windows", StringComparison.OrdinalIgnoreCase))
            desc = procName;
        if (string.IsNullOrEmpty(desc)) return Desktop;
        var s = Sanitize(desc);
        return s.Length == 0 ? Desktop : s;
    }

    static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SearchHost", "SearchApp", "ShellExperienceHost", "StartMenuExperienceHost",
        "TextInputHost", "LockApp", "ScreenClippingHost", "dwm", "Idle", "csrss",
    };

    public static string Sanitize(string s, int maxLength = 40)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsControl(ch) || IsInvisibleMark(ch)) continue;
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
        }
        var r = sb.ToString().Trim().TrimEnd('.');
        while (r.Contains("  ")) r = r.Replace("  ", " ");
        if (r.Length > maxLength)
        {
            var cut = char.IsHighSurrogate(r[maxLength - 1]) ? maxLength - 1 : maxLength;
            r = r[..cut].TrimEnd();
        }
        return r;
    }

    static bool IsInvisibleMark(char ch) => ch is '‎' or '‏' or '؜' or '﻿' or '​' or '‌'
        or (>= '‪' and <= '‮') or (>= '⁠' and <= '⁩')
        or 'ㅤ' or 'ﾠ' or 'ᅟ' or 'ᅠ' or '⠀' or '­';

    static string? GetProcessPath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var len = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString(0, len) : null;
        }
        finally { CloseHandle(h); }
    }

    static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static bool IsCloaked(IntPtr hwnd)
    {
        try { return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0; }
        catch { return false; }
    }

    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, DWMWA_CLOAKED = 14;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder exe, ref int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}

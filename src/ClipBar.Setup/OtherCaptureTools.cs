using System.Diagnostics;
using Microsoft.Win32;

namespace ClipBar.Setup;

public static class OtherCaptureTools
{
    public enum Kind { Win32, Appx, Setting }

    public sealed record Tool(string Name, string Note, Kind Kind, string Target);

    static readonly (string Match, string Note)[] Known =
    [
        ("Lightshot", "скриншоты"), ("ShareX", "скриншоты и запись"), ("Greenshot", "скриншоты"), ("Snagit", "скриншоты и запись"),
        ("PicPick", "скриншоты"), ("Gyazo", "скриншоты"), ("Joxi", "скриншоты"), ("Screenpresso", "скриншоты"),
        ("FastStone Capture", "скриншоты"), ("Monosnap", "скриншоты"), ("Nimbus", "скриншоты"),
        ("Bandicam", "запись экрана"), ("Movavi Screen", "запись экрана"), ("Fraps", "запись экрана"), ("Mirillis Action", "запись экрана"),
        ("Icecream Screen", "запись экрана"), ("ScreenRec", "запись экрана"), ("Medal", "клипы"), ("Outplayed", "клипы"),
        ("OBS Studio", "запись и стримы"),
    ];

    static readonly (string Package, string Name, string Note)[] KnownAppx =
    [
        ("Microsoft.ScreenSketch", "Ножницы (Snipping Tool)", "встроенный скриншотер Windows"),
        ("Microsoft.XboxGamingOverlay", "Xbox Game Bar", "встроенная запись Windows"),
    ];

    public static IReadOnlyList<Tool> Detect()
    {
        var found = new List<Tool>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, path) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            using var root = hive.OpenSubKey(path);
            if (root is null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is not string name || (k.GetValue("SystemComponent") as int?) == 1) continue;
                var cmd = k.GetValue("UninstallString") as string;
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                var hit = Known.FirstOrDefault(x => name.Contains(x.Match, StringComparison.OrdinalIgnoreCase));
                if (hit.Match is null || !seen.Add(hit.Match)) continue;
                found.Add(new Tool(name, hit.Note, Kind.Win32, cmd));
            }
        }

        foreach (var (pkg, name, note) in KnownAppx)
            if (AppxInstalled(pkg)) found.Add(new Tool(name, note, Kind.Appx, pkg));

        using (var kb = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard"))
        {
            var v = kb?.GetValue("PrintScreenKeyForSnippingEnabled");
            if (v is null || (v is int i && i != 0))
                found.Add(new Tool("Клавиша PrintScreen → захват Windows", "PrintScreen будет открывать скриншот ClipBar", Kind.Setting, "PrintScreenKeyForSnippingEnabled"));
        }
        return found;
    }

    static bool AppxInstalled(string package)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"if (Get-AppxPackage -Name '{package}') {{ exit 0 }} else {{ exit 1 }}\"")
            { CreateNoWindow = true, UseShellExecute = false });
            if (p is null) return false;
            p.WaitForExit(15000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static async Task<bool> RemoveAsync(Tool t)
    {
        try
        {
            switch (t.Kind)
            {
                case Kind.Setting:
                    using (var kb = Registry.CurrentUser.CreateSubKey(@"Control Panel\Keyboard", writable: true))
                        kb.SetValue(t.Target, 0, RegistryValueKind.DWord);
                    return true;

                case Kind.Appx:
                {
                    using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                        $"-NoProfile -NonInteractive -Command \"Get-AppxPackage -Name '{t.Target}' | Remove-AppxPackage\"")
                    { CreateNoWindow = true, UseShellExecute = false });
                    if (p is null) return false;
                    await p.WaitForExitAsync();
                    return p.ExitCode == 0;
                }

                default:
                {
                    var (exe, args) = SplitCommand(t.Target);
                    using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
                    if (p is null) return false;
                    await p.WaitForExitAsync();
                    return true;
                }
            }
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch { return false; }
    }

    static (string Exe, string Args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return end > 0 ? (cmd[1..end], cmd[(end + 1)..].Trim()) : (cmd.Trim('"'), "");
        }
        var exeEnd = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeEnd > 0 ? (cmd[..(exeEnd + 4)], cmd[(exeEnd + 4)..].Trim()) : (cmd, "");
    }
}

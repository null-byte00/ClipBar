using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ClipBar.Setup;

public static class SystemCheck
{
    public sealed record Item(string Title, bool Ok, string Detail, bool CanFix);

    const int MinBuild = 19041;

    const string MediaPackCapability = "Media.MediaFeaturePack~~~~0.0.1.0";

    public static IReadOnlyList<Item> Run()
    {
        var build = Environment.OSVersion.Version.Build;
        var items = new List<Item>
        {
            build >= MinBuild
                ? new Item("Windows", true, $"{WindowsName()} (сборка {build})", false)
                : new Item("Windows", false, $"Сборка {build} — нужна Windows 10 версии 2004 или новее. Запись будет работать, но оверлей может попадать в видео.", false),
            HasMediaFoundation()
                ? new Item("Воспроизведение видео", true, "Media Foundation есть", false)
                : new Item("Воспроизведение видео", false, "Нет Media Foundation (Windows N). Без него не работает просмотр клипов в редакторе — запись и экспорт работают.", true),
            Environment.Is64BitOperatingSystem
                ? new Item("Разрядность", true, "64-bit", false)
                : new Item("Разрядность", false, "Нужна 64-битная Windows.", false),
        };
        return items;
    }

    public static bool CanInstall => Environment.Is64BitOperatingSystem;

    static bool HasMediaFoundation() =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mfplat.dll"));

    public static async Task<bool> InstallMediaFeaturePackAsync()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("dism.exe", $"/online /add-capability /capabilityname:{MediaPackCapability} /norestart")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode is 0 or 3010;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    static string WindowsName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = key?.GetValue("ProductName") as string ?? "Windows";
            if (Environment.OSVersion.Version.Build >= 22000) name = name.Replace("Windows 10", "Windows 11");
            var display = key?.GetValue("DisplayVersion") as string;
            return display is null ? name : $"{name} {display}";
        }
        catch { return "Windows"; }
    }
}

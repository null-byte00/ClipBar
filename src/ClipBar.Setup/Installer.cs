using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace ClipBar.Setup;

public sealed record InstallOptions(string Directory, bool DesktopShortcut, bool Autostart, bool LaunchAfter);

public static class Installer
{
    public const string AppName = "ClipBar";
    public const string Publisher = "@lumaseller";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipBar";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string? ExistingInstall()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") is string dir && File.Exists(Path.Combine(dir, "ClipBar.exe")) ? dir : null;
    }

    public static string? ExistingVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("DisplayVersion") as string;
    }

    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo("payload.zip") is not null;

    public static async Task InstallAsync(InstallOptions o, IProgress<(double Fraction, string Status)> progress)
    {
        await Task.Run(() =>
        {
            progress.Report((0.02, "Закрываю запущенный ClipBar…"));
            StopRunningClipBar(o.Directory);

            progress.Report((0.05, "Распаковываю файлы…"));
            Directory.CreateDirectory(o.Directory);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                                ?? throw new InvalidOperationException("В установщике нет файлов программы (payload.zip)."))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var baseDir = Path.GetFullPath(o.Directory).TrimEnd('\\') + "\\";
                var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long total = zip.Entries.Sum(e => e.Length), done = 0;
                foreach (var entry in zip.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(o.Directory, entry.FullName));
                    if (!target.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Некорректный путь в архиве: " + entry.FullName);
                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    WriteWithRetry(entry, target);
                    written.Add(target);
                    done += entry.Length;
                    progress.Report((0.05 + 0.85 * done / Math.Max(1, total), "Распаковываю файлы…"));
                }

                PruneOrphans(o.Directory, written);
            }

            var exe = Path.Combine(o.Directory, "ClipBar.exe");
            progress.Report((0.92, "Создаю ярлыки…"));
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ClipBar.lnk"), exe);
            var desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ClipBar.lnk");
            if (o.DesktopShortcut) CreateShortcut(desktopLnk, exe);
            else TryDelete(desktopLnk);

            progress.Report((0.96, "Регистрирую программу…"));
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
            {
                if (o.Autostart) run.SetValue("ClipBar", $"\"{exe}\"");
                else run.DeleteValue("ClipBar", throwOnMissingValue: false);
            }
            RegisterUninstall(o.Directory, exe);

            progress.Report((1, "Готово"));
        });

        if (o.LaunchAfter)
            Process.Start(new ProcessStartInfo(Path.Combine(o.Directory, "ClipBar.exe")) { UseShellExecute = true, WorkingDirectory = o.Directory });
    }

    static void StopRunningClipBar(string dir)
    {
        var full = Path.GetFullPath(dir).TrimEnd('\\');
        foreach (var p in Process.GetProcessesByName("ClipBar"))
        {
            try
            {
                // Трогаем только копию из целевой папки — чужие/портативные копии не убиваем.
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                if (path is not null &&
                    !Path.GetFullPath(path).StartsWith(full, StringComparison.OrdinalIgnoreCase))
                    continue;

                p.Kill(entireProcessTree: true);
                if (!p.WaitForExit(10000))
                    throw new IOException("Не удалось закрыть запущенный ClipBar. Закрой программу и повтори.");
            }
            catch (IOException) { throw; }
            catch { }
            finally { p.Dispose(); }
        }
    }

    static void WriteWithRetry(ZipArchiveEntry entry, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { entry.ExtractToFile(target, overwrite: true); return; }
            catch (IOException) when (attempt < 40) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) when (attempt < 40) { Thread.Sleep(500); }
        }
    }

    // Удаляет файлы прошлой версии, которых нет в новой раскладке (чтобы не копились и не грузились старые сборки).
    static void PruneOrphans(string dir, HashSet<string> written)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(f);
                if (!written.Contains(full)) { try { File.Delete(full); } catch { } }
            }
        }
        catch { }
    }

    static void RegisterUninstall(string dir, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, writable: true);
        key.SetValue("DisplayName", "ClipBar");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("DisplayIcon", $"\"{exe}\",0");
        key.SetValue("InstallLocation", dir);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(DirectorySize(dir) / 1024), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    static long DirectorySize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch { return 0; }
    }

    static void CreateShortcut(string lnkPath, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступен");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = Path.GetDirectoryName(target);
            lnk.IconLocation = target + ",0";
            lnk.Description = "ClipBar — запись экрана с мгновенным повтором";
            lnk.Save();
        }
        catch (Exception ex) { Debug.WriteLine("Shortcut failed: " + ex.Message); }
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}

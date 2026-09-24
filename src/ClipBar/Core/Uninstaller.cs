using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ClipBar.Core;

public static class Uninstaller
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipBar";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static async Task RunAsync(bool quiet)
    {
        var removeSettings = false;
        if (!quiet)
        {
            var keepData = new CheckBox { Content = "Удалить и настройки ClipBar (клипы не трогаются никогда)", Margin = new Thickness(0, 12, 0, 0) };
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Удалить ClipBar?",
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "ClipBar будет удалён с компьютера. Твои клипы и скриншоты останутся в папке.", TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
                        keepData,
                    },
                },
                PrimaryButtonText = "Удалить",
                PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
                CloseButtonText = "Отмена",
            };
            if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
            removeSettings = keepData.IsChecked == true;
        }

        var dir = AppContext.BaseDirectory.TrimEnd('\\');
        string? installed;
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey)) installed = (key?.GetValue("InstallLocation") as string)?.TrimEnd('\\');
        var deleteFolder = installed is not null && string.Equals(installed, dir, StringComparison.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcessesByName("ClipBar"))
                if (p.Id != Environment.ProcessId) { try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { } finally { p.Dispose(); } }

            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)) run?.DeleteValue("ClipBar", throwOnMissingValue: false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ClipBar.lnk"));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ClipBar.lnk"));

            TryDeleteDir(AppPaths.DataDir);
            if (removeSettings) TryDeleteDir(Path.GetDirectoryName(AppPaths.SettingsFile)!);

            if (deleteFolder)
            {
                Process.Start(new ProcessStartInfo("cmd.exe",
                    $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{dir}\"")
                { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
            }

            if (!quiet)
                await new Wpf.Ui.Controls.MessageBox { Title = "ClipBar удалён", Content = "Спасибо, что пользовался ClipBar by @lumaseller!", CloseButtonText = "OK" }.ShowDialogAsync();
        }
        catch (Exception ex)
        {
            if (!quiet)
                await new Wpf.Ui.Controls.MessageBox { Title = "Не удалось удалить до конца", Content = ex.Message, CloseButtonText = "OK" }.ShowDialogAsync();
        }
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }
}

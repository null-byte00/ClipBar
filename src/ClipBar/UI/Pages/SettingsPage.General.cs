using System.Windows;
using ClipBar.Core;
using Microsoft.Win32;

namespace ClipBar.UI.Pages;

public partial class SettingsPage
{
    void LoadGeneral()
    {
        var actual = StartupRegistry.IsEnabled();
        if (S.LaunchAtStartup != actual) S.LaunchAtStartup = actual;
        StartupToggle.IsChecked = actual;
        MinimizedToggle.IsChecked = S.StartMinimized;
        NotificationsToggle.IsChecked = S.ShowNotifications;
        SoundsToggle.IsChecked = S.PlaySounds;
    }

    void OnStartupToggleClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = StartupToggle.IsChecked == true;
        try
        {
            StartupRegistry.Set(on);
            S.LaunchAtStartup = on;
            Save();
        }
        catch (Exception ex)
        {
            Log.Error("Startup registry write failed", ex);
            AppServices.Notifier?.Show("Не удалось изменить автозапуск", ex.Message, NotifyKind.Error);
            Guarded(() => StartupToggle.IsChecked = StartupRegistry.IsEnabled());
        }
    }

    void OnMinimizedToggleClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.StartMinimized = MinimizedToggle.IsChecked == true;
        Save();
    }

    void OnNotificationsToggleClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ShowNotifications = NotificationsToggle.IsChecked == true;
        Save();
    }

    void OnSoundsToggleClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.PlaySounds = SoundsToggle.IsChecked == true;
        Save();
    }
}

internal static class StartupRegistry
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ClipBar";

    public static string ExePath =>
        Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "ClipBar.exe";

    public static string Command => $"\"{ExePath}\"";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Startup registry read failed: " + ex.Message);
            return false;
        }
    }

    public static string? CurrentValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                        ?? throw new InvalidOperationException("Не удалось открыть раздел автозапуска");
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

using System.IO;

namespace ClipBar.Core;

public static class Actions
{
    static readonly SemaphoreSlim SaveGate = new(1, 1);
    static readonly SemaphoreSlim RecordGate = new(1, 1);

    public static Task RunAsync(HotkeyAction action) => action switch
    {
        HotkeyAction.SaveReplay => SaveReplayAsync(),
        HotkeyAction.ToggleRecording => ToggleRecordingAsync(),
        HotkeyAction.Screenshot => ScreenshotAsync(),
        HotkeyAction.RegionScreenshot or HotkeyAction.RegionScreenshotKey => RegionScreenshotAsync(),
        HotkeyAction.ToggleMic => Task.Run(ToggleMic),
        HotkeyAction.ToggleOverlay => Task.Run(() => AppServices.Shell.ToggleOverlay()),
        HotkeyAction.ToggleReplayBuffer => ToggleReplayBufferAsync(),
        _ => Task.CompletedTask,
    };

    public static async Task SaveReplayAsync(TimeSpan? duration = null)
    {
        var engine = AppServices.Engine;
        if (!engine.ReplayActive)
        {
            AppServices.Notifier.Show("Откат выключен",
                $"Включи буфер отката ({HotkeyText(HotkeyAction.ToggleReplayBuffer)})", NotifyKind.Warning);
            return;
        }
        if (!await SaveGate.WaitAsync(0)) return;
        try
        {
            var path = await engine.SaveReplayAsync(duration);
            await AppServices.Library.AddAsync(path);
            AppServices.Notifier.Show("Откат сохранён", Path.GetFileName(path), NotifyKind.Success, path);
        }
        catch (Exception ex)
        {
            Log.Error("SaveReplay failed", ex);
            AppServices.Notifier.Show("Не удалось сохранить откат", ex.Message, NotifyKind.Error);
        }
        finally { SaveGate.Release(); }
    }

    public static async Task ToggleRecordingAsync()
    {
        if (!await RecordGate.WaitAsync(0)) return;
        try
        {
            var engine = AppServices.Engine;
            if (!engine.IsRecording)
            {
                await engine.StartRecordingAsync();
                AppServices.Notifier.Show("Запись началась",
                    $"Остановить: {HotkeyText(HotkeyAction.ToggleRecording)}", NotifyKind.Recording);
            }
            else
            {
                var path = await engine.StopRecordingAsync();
                await AppServices.Library.AddAsync(path);
                AppServices.Notifier.Show("Запись сохранена", Path.GetFileName(path), NotifyKind.Success, path);
            }
        }
        catch (Exception ex)
        {
            Log.Error("ToggleRecording failed", ex);
            AppServices.Notifier.Show("Ошибка записи", ex.Message, NotifyKind.Error);
        }
        finally { RecordGate.Release(); }
    }

    public static async Task ScreenshotAsync()
    {
        try
        {
            var path = await AppServices.Engine.TakeScreenshotAsync();
            await AppServices.Library.AddAsync(path);
            AppServices.Notifier.Show("Скриншот сохранён", Path.GetFileName(path), NotifyKind.Success, path);
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot failed", ex);
            AppServices.Notifier.Show("Не удалось сделать скриншот", ex.Message, NotifyKind.Error);
        }
    }

    public static Task RegionScreenshotAsync()
    {
        try { AppServices.Shell.StartRegionScreenshot(); }
        catch (Exception ex)
        {
            Log.Error("Region screenshot failed", ex);
            AppServices.Notifier.Show("Не удалось сделать скриншот", ex.Message, NotifyKind.Error);
        }
        return Task.CompletedTask;
    }

    public static void ToggleMic()
    {
        var audio = AppServices.Audio;
        audio.MicMuted = !audio.MicMuted;
        AppServices.Settings.Save();
        AppServices.Notifier.Show(audio.MicMuted ? "Микрофон выключен" : "Микрофон включён", "", NotifyKind.Info);
    }

    public static async Task ToggleReplayBufferAsync()
    {
        try
        {
            var engine = AppServices.Engine;
            var enable = !engine.ReplayActive;
            if (enable) await engine.StartReplayAsync();
            else await engine.StopReplayAsync();
            AppServices.Settings.Current.ReplayEnabled = enable;
            AppServices.Settings.Save();
            AppServices.Notifier.Show(enable ? "Откат включён" : "Откат выключен",
                enable ? $"Хранятся последние {FormatDuration(TimeSpan.FromSeconds(AppServices.Settings.Current.ReplaySeconds))}" : "",
                NotifyKind.Info);
        }
        catch (Exception ex)
        {
            Log.Error("ToggleReplayBuffer failed", ex);
            AppServices.Notifier.Show("Ошибка буфера отката", ex.Message, NotifyKind.Error);
        }
    }

    public static string HotkeyText(HotkeyAction action) =>
        AppServices.Settings.Current.Hotkeys.TryGetValue(action, out var s) && !string.IsNullOrEmpty(s) ? s : "не назначено";

    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds} сек";
        if (t.TotalMinutes < 60)
            return t.Seconds == 0 ? $"{(int)t.TotalMinutes} мин" : $"{(int)t.TotalMinutes} мин {t.Seconds} сек";
        return t.Minutes == 0 ? $"{(int)t.TotalHours} ч" : $"{(int)t.TotalHours} ч {t.Minutes} мин";
    }
}

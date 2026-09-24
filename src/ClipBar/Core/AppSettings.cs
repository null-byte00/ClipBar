using System.IO;

namespace ClipBar.Core;

public sealed class AppSettings
{
    public bool ReplayEnabled { get; set; } = true;
    public int ReplaySeconds { get; set; } = 300;

    public int MonitorIndex { get; set; } = 0;
    public int Fps { get; set; } = 60;
    public int OutputHeight { get; set; } = 0;
    public string Encoder { get; set; } = "auto";
    public int VideoBitrateKbps { get; set; } = 20000;
    public bool CaptureCursor { get; set; } = true;

    public bool RecordSystemAudio { get; set; } = true;
    public string? SystemDeviceId { get; set; }
    public bool RecordMic { get; set; } = true;
    public string? MicDeviceId { get; set; }
    public float SystemVolume { get; set; } = 1f;
    public float MicVolume { get; set; } = 1f;
    public bool MicMuted { get; set; }
    public bool SeparateAudioTracks { get; set; } = true;
    public int AudioBitrateKbps { get; set; } = 192;
    public int AudioOffsetMs { get; set; }

    public string ClipsFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ClipBar");
    public bool UseAppNameInFileName { get; set; } = true;

    public bool LaunchAtStartup { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool PlaySounds { get; set; } = true;

    public Dictionary<HotkeyAction, string> Hotkeys { get; set; } = DefaultHotkeys();

    public static Dictionary<HotkeyAction, string> DefaultHotkeys() => new()
    {
        [HotkeyAction.SaveReplay] = "Alt+F10",
        [HotkeyAction.ToggleRecording] = "Alt+F9",
        [HotkeyAction.Screenshot] = "Alt+F1",
        [HotkeyAction.RegionScreenshot] = "Win+Shift+S",
        [HotkeyAction.RegionScreenshotKey] = "PrintScreen",
        [HotkeyAction.ToggleMic] = "Alt+F8",
        [HotkeyAction.ToggleOverlay] = "Alt+Z",
        [HotkeyAction.ToggleReplayBuffer] = "Alt+Shift+F10",
    };

    public AppSettings Clone() =>
        System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(this, SettingsService.JsonOptions), SettingsService.JsonOptions)!;
}

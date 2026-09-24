namespace ClipBar.Core;

public static class AppServices
{
    public static SettingsService Settings { get; set; } = null!;
    public static ICaptureEngine Engine { get; set; } = null!;
    public static IAudioHub Audio { get; set; } = null!;
    public static IHotkeyService Hotkeys { get; set; } = null!;
    public static IClipLibrary Library { get; set; } = null!;
    public static INotifier Notifier { get; set; } = null!;
    public static IShell Shell { get; set; } = null!;
}

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipBar.Core;

public sealed class SettingsService
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Current { get; private set; } = new();
    readonly object _saveLock = new();

    public event EventHandler<AppSettings>? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Settings load failed, using defaults", ex);
            Current = new();
        }

        foreach (var (action, binding) in AppSettings.DefaultHotkeys())
            Current.Hotkeys.TryAdd(action, binding);

        if (Current.Hotkeys.TryGetValue(HotkeyAction.ToggleOverlay, out var overlayKey) && overlayKey == "Alt+G")
            Current.Hotkeys[HotkeyAction.ToggleOverlay] = "Alt+Z";

        Current.ReplaySeconds = Math.Clamp(Current.ReplaySeconds, 15, 3600);
        Current.SystemVolume = Math.Clamp(Current.SystemVolume, 0f, 2f);
        Current.MicVolume = Math.Clamp(Current.MicVolume, 0f, 2f);
    }

    public bool Save()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsFile)!);
                var tmp = AppPaths.SettingsFile + $".{Environment.CurrentManagedThreadId}.tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
                File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("Settings save failed", ex);
                return false;
            }
        }
        Changed?.Invoke(this, Current);
        return true;
    }
}

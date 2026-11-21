using System.Windows.Input;

namespace ClipBar.Core;

public enum HotkeyAction
{
    SaveReplay,
    ToggleRecording,
    Screenshot,
    ToggleMic,
    ToggleOverlay,
    ToggleReplayBuffer,
    RegionScreenshot,
    RegionScreenshotKey,
}

public static class HotkeyActionNames
{
    public static string Title(this HotkeyAction a) => a switch
    {
        HotkeyAction.SaveReplay => "Сохранить откат",
        HotkeyAction.ToggleRecording => "Начать / остановить запись",
        HotkeyAction.Screenshot => "Скриншот",
        HotkeyAction.RegionScreenshot => "Скриншот области",
        HotkeyAction.RegionScreenshotKey => "Скриншот области (2-я клавиша)",
        HotkeyAction.ToggleMic => "Микрофон вкл / выкл",
        HotkeyAction.ToggleOverlay => "Открыть оверлей",
        HotkeyAction.ToggleReplayBuffer => "Буфер отката вкл / выкл",
        _ => a.ToString(),
    };
}

public sealed record HotkeyBinding(ModifierKeys Modifiers, Key Key, MouseButton? Mouse = null)
{
    public static readonly HotkeyBinding None = new(ModifierKeys.None, Key.None);

    public bool IsEmpty => Key == Key.None && Mouse is null;

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key != Key.None ? KeyName(Key) : MouseName(Mouse!.Value));
        return string.Join("+", parts);
    }

    public static HotkeyBinding Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        var mods = ModifierKeys.None;
        var key = Key.None;
        MouseButton? mouse = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win" or "windows": mods |= ModifierKeys.Windows; break;
                default:
                    key = ParseKey(raw);
                    if (key == Key.None) mouse = ParseMouse(raw);
                    break;
            }
        }
        return key == Key.None && mouse is null ? None : new HotkeyBinding(mods, key, mouse);
    }

    static string MouseName(MouseButton b) => b switch
    {
        MouseButton.Left => "Mouse1",
        MouseButton.Right => "Mouse2",
        MouseButton.Middle => "Mouse3",
        MouseButton.XButton1 => "Mouse4",
        MouseButton.XButton2 => "Mouse5",
        _ => b.ToString(),
    };

    static MouseButton? ParseMouse(string s) => s.ToLowerInvariant() switch
    {
        "mouse1" => MouseButton.Left,
        "mouse2" => MouseButton.Right,
        "mouse3" or "mousemiddle" => MouseButton.Middle,
        "mouse4" or "xbutton1" => MouseButton.XButton1,
        "mouse5" or "xbutton2" => MouseButton.XButton2,
        _ => null,
    };

    static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (int)(k - Key.NumPad0),
        Key.OemTilde => "~",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.Snapshot => "PrintScreen",
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        _ => k.ToString(),
    };

    static Key ParseKey(string s)
    {
        if (s.Length == 1 && char.IsDigit(s[0])) return Key.D0 + (s[0] - '0');
        if (s.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && s.Length == 4 && char.IsDigit(s[3]))
            return Key.NumPad0 + (s[3] - '0');
        return s switch
        {
            "~" => Key.OemTilde,
            "-" => Key.OemMinus,
            "=" => Key.OemPlus,
            _ => Enum.TryParse<Key>(s, true, out var k) ? k : Key.None,
        };
    }
}

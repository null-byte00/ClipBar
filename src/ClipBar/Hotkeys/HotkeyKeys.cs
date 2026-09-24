using System.Windows.Input;

namespace ClipBar.Hotkeys;

public static class HotkeyKeys
{
    public static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    public static ModifierKeys ModifierOf(Key k) => k switch
    {
        Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
        Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
        Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
        Key.LWin or Key.RWin => ModifierKeys.Windows,
        _ => ModifierKeys.None,
    };

    public static bool AllowsWithoutModifier(Key k) =>
        (k >= Key.F1 && k <= Key.F24) || k is Key.Snapshot or Key.Pause or Key.Scroll or Key.Insert;

    internal static uint ToNativeModifiers(ModifierKeys m)
    {
        uint r = 0;
        if (m.HasFlag(ModifierKeys.Control)) r |= HotkeyNative.MOD_CONTROL;
        if (m.HasFlag(ModifierKeys.Alt)) r |= HotkeyNative.MOD_ALT;
        if (m.HasFlag(ModifierKeys.Shift)) r |= HotkeyNative.MOD_SHIFT;
        if (m.HasFlag(ModifierKeys.Windows)) r |= HotkeyNative.MOD_WIN;
        return r;
    }

    public static IEnumerable<string> ModifierLabels(ModifierKeys m)
    {
        if (m.HasFlag(ModifierKeys.Control)) yield return "Ctrl";
        if (m.HasFlag(ModifierKeys.Alt)) yield return "Alt";
        if (m.HasFlag(ModifierKeys.Shift)) yield return "Shift";
        if (m.HasFlag(ModifierKeys.Windows)) yield return "Win";
    }

    public static string DisplayName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(k - Key.NumPad0),
        >= Key.A and <= Key.Z => k.ToString(),
        >= Key.F1 and <= Key.F24 => k.ToString(),
        Key.Snapshot => "PrtSc",
        Key.Scroll => "ScrLk",
        Key.Pause => "Pause",
        Key.Insert => "Ins",
        Key.Delete => "Del",
        Key.Home => "Home",
        Key.End => "End",
        Key.Prior => "PgUp",
        Key.Next => "PgDn",
        Key.Back => "Backspace",
        Key.Tab => "Tab",
        Key.Return => "Enter",
        Key.Space => "Space",
        Key.Escape => "Esc",
        Key.CapsLock => "Caps",
        Key.NumLock => "NumLk",
        Key.Apps => "Menu",
        Key.Left => "←",
        Key.Up => "↑",
        Key.Right => "→",
        Key.Down => "↓",
        Key.Add => "Num +",
        Key.Subtract => "Num -",
        Key.Multiply => "Num *",
        Key.Divide => "Num /",
        Key.Decimal => "Num .",
        Key.OemTilde => "~",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemBackslash => "\\",
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.LWin or Key.RWin => "Win",
        _ => k.ToString(),
    };
}

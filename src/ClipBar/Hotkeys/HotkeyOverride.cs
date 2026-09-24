using System.Runtime.InteropServices;
using System.Windows.Input;
using ClipBar.Core;

namespace ClipBar.Hotkeys;

internal sealed class HotkeyOverride : IDisposable
{
    readonly KeyboardCaptureHook _hook = new();
    readonly Action<HotkeyAction> _fire;
    Dictionary<(ModifierKeys, Key), HotkeyAction> _combos = new();
    readonly HashSet<Key> _held = new();

    public HotkeyOverride(Action<HotkeyAction> fire)
    {
        _fire = fire;
        _hook.Handler = OnKey;
    }

    public bool Suspended { get; set; }

    public IReadOnlyList<HotkeyAction> Set(IEnumerable<(HotkeyAction Action, HotkeyBinding Binding)> bindings)
    {
        _combos = bindings.ToDictionary(b => (b.Binding.Modifiers, b.Binding.Key), b => b.Action);
        _held.Clear();
        if (_combos.Count == 0) { _hook.Uninstall(); return []; }
        if (_hook.Install())
        {
            Log.Info("HotkeyOverride: intercepting " + string.Join(", ", _combos.Select(c => $"{c.Value}={new HotkeyBinding(c.Key.Item1, c.Key.Item2)}")));
            return [];
        }
        return _combos.Values.ToList();
    }

    bool OnKey(Key key, bool down, bool injected)
    {
        if (Suspended || IsModifier(key)) return false;
        if (!down)
        {
            return _held.Remove(key);
        }
        if (!_combos.TryGetValue((CurrentModifiers(), key), out var action)) return false;

        if (_held.Add(key))
        {
            if (IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN)) { keybd_event(VK_NONAME, 0, 0, 0); keybd_event(VK_NONAME, 0, KEYEVENTF_KEYUP, 0); }
            _fire(action);
        }
        return true;
    }

    static bool IsModifier(Key k) => k is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    static ModifierKeys CurrentModifiers()
    {
        var m = ModifierKeys.None;
        if (IsDown(VK_MENU)) m |= ModifierKeys.Alt;
        if (IsDown(VK_CONTROL)) m |= ModifierKeys.Control;
        if (IsDown(VK_SHIFT)) m |= ModifierKeys.Shift;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) m |= ModifierKeys.Windows;
        return m;
    }

    static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose() => _hook.Dispose();

    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    const byte VK_NONAME = 0xFC;
    const uint KEYEVENTF_KEYUP = 2;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
}

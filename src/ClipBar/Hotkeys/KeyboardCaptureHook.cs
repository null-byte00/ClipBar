using System.Runtime.InteropServices;
using System.Windows.Input;
using ClipBar.Core;
using static ClipBar.Hotkeys.HotkeyNative;

namespace ClipBar.Hotkeys;

internal sealed class KeyboardCaptureHook : IDisposable
{
    readonly LowLevelKeyboardProc _proc;
    readonly nint _procPtr;
    nint _hook;

    public Func<Key, bool, bool, bool>? Handler { get; set; }

    public bool IsInstalled => _hook != 0;

    public KeyboardCaptureHook()
    {
        _proc = Callback;
        _procPtr = Marshal.GetFunctionPointerForDelegate(_proc);
    }

    public bool Install()
    {
        if (_hook != 0) return true;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _procPtr, GetModuleHandleW(null), 0);
        if (_hook == 0)
            Log.Warn($"KeyboardCaptureHook: SetWindowsHookEx failed: {DescribeError(Marshal.GetLastPInvokeError())}");
        return _hook != 0;
    }

    public void Uninstall()
    {
        if (_hook == 0) return;
        UnhookWindowsHookEx(_hook);
        _hook = 0;
    }

    public void Dispose()
    {
        Uninstall();
        GC.KeepAlive(_proc);
    }

    nint Callback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && Handler is { } handler)
        {
            var msg = (int)wParam;
            var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            if (down || msg is WM_KEYUP or WM_SYSKEYUP)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var key = KeyInterop.KeyFromVirtualKey((int)data.vkCode);
                var injected = (data.flags & LLKHF_INJECTED) != 0;
                bool swallow;
                try { swallow = handler(key, down, injected); }
                catch (Exception ex) { Log.Error("KeyboardCaptureHook handler failed", ex); swallow = false; }
                if (swallow) return 1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}

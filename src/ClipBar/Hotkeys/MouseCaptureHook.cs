using System.Runtime.InteropServices;
using System.Windows.Input;
using ClipBar.Core;
using static ClipBar.Hotkeys.HotkeyNative;

namespace ClipBar.Hotkeys;

internal sealed class MouseCaptureHook : IDisposable
{
    readonly LowLevelMouseProc _proc;
    readonly nint _procPtr;
    nint _hook;

    /// <summary>(button, down, injected) → true = проглотить клик.</summary>
    public Func<MouseButton, bool, bool, bool>? Handler { get; set; }

    public bool IsInstalled => _hook != 0;

    public MouseCaptureHook()
    {
        _proc = Callback;
        _procPtr = Marshal.GetFunctionPointerForDelegate(_proc);
    }

    public bool Install()
    {
        if (_hook != 0) return true;
        _hook = SetWindowsHookExW(WH_MOUSE_LL, _procPtr, GetModuleHandleW(null), 0);
        if (_hook == 0)
            Log.Warn($"MouseCaptureHook: SetWindowsHookEx failed: {DescribeError(Marshal.GetLastPInvokeError())}");
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
            MouseButton? btn = msg switch
            {
                WM_XBUTTONDOWN or WM_XBUTTONUP => Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).mouseData >> 16 == XBUTTON1
                    ? MouseButton.XButton1 : MouseButton.XButton2,
                WM_MBUTTONDOWN or WM_MBUTTONUP => MouseButton.Middle,
                _ => null,
            };
            if (btn is { } b)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var down = msg is WM_XBUTTONDOWN or WM_MBUTTONDOWN;
                var injected = (data.flags & LLMHF_INJECTED) != 0;
                bool swallow;
                try { swallow = handler(b, down, injected); }
                catch (Exception ex) { Log.Error("MouseCaptureHook handler failed", ex); swallow = false; }
                if (swallow) return 1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipBar.Core;
using static ClipBar.Hotkeys.HotkeyNative;

namespace ClipBar.Hotkeys;

public sealed class HotkeyService : IHotkeyService
{
    const int IdBase = 0x4B00;

    HwndSource? _source;
    Dispatcher? _dispatcher;
    bool _disposed;
    bool _suspended;

    readonly Dictionary<int, HotkeyAction> _registered = new();
    Dictionary<HotkeyAction, HotkeyBinding> _last = new();
    HotkeyAction[] _failed = [];

    public IReadOnlyList<HotkeyAction> FailedActions => _failed;

    public event EventHandler<HotkeyAction>? Pressed;

    public nint Handle => _source?.Handle ?? 0;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source is not null) return;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _source = CreateWindow();
        _source.AddHook(WndProc);
        Log.Info($"HotkeyService: message window 0x{_source.Handle:X} created");
    }

    static HwndSource CreateWindow()
    {
        try
        {
            return new HwndSource(new HwndSourceParameters("ClipBar.Hotkeys")
            {
                ParentWindow = HWND_MESSAGE, WindowStyle = 0, ExtendedWindowStyle = 0,
                Width = 0, Height = 0, PositionX = 0, PositionY = 0,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"HotkeyService: message-only window failed ({ex.Message}), using hidden top-level window");
            return new HwndSource(new HwndSourceParameters("ClipBar.Hotkeys")
            {
                WindowStyle = 0, ExtendedWindowStyle = 0x80,
                Width = 0, Height = 0, PositionX = -32000, PositionY = -32000,
            });
        }
    }

    public IReadOnlyList<HotkeyAction> Apply(IReadOnlyDictionary<HotkeyAction, HotkeyBinding> bindings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bindings);
        if (_source is null)
        {
            var d = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            if (!d.CheckAccess()) return d.Invoke(() => Apply(bindings));
            Initialize();
        }
        if (!_dispatcher!.CheckAccess()) return _dispatcher.Invoke(() => Apply(bindings));

        _last = bindings.ToDictionary(kv => kv.Key, kv => kv.Value ?? HotkeyBinding.None);
        UnregisterAll();
        RegisterLast();
        if (_suspended) UnregisterAll();
        return _failed;
    }

    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_disposed || _suspended == value) return;
            if (_dispatcher is { } d && !d.CheckAccess()) { d.Invoke(() => Suspended = value); return; }
            _suspended = value;
            if (_source is null) return;
            if (value) UnregisterAll();
            else RegisterLast();
            Log.Info(value ? "HotkeyService: suspended" : "HotkeyService: resumed");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_dispatcher is { } d && !d.CheckAccess() && !d.HasShutdownStarted) { d.Invoke(Dispose); return; }
        _disposed = true;
        try
        {
            UnregisterAll();
            _override?.Dispose();
            if (_source is not null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }
        catch (Exception ex) { Log.Error("HotkeyService dispose failed", ex); }
    }

    void RegisterLast()
    {
        var hwnd = _source!.Handle;
        var failed = new List<HotkeyAction>();
        var takenByOthers = new List<(HotkeyAction, HotkeyBinding)>();
        var seenCombos = new HashSet<(ModifierKeys, Key)>();
        foreach (var (action, binding) in _last)
        {
            if (binding.IsEmpty) continue;

            if (!seenCombos.Add((binding.Modifiers, binding.Key)))
            {
                failed.Add(action);
                Log.Warn($"HotkeyService: {action} = {binding} not registered: same combo already used by another ClipBar action");
                continue;
            }

            var id = IdBase + (int)action;
            int vk;
            try { vk = KeyInterop.VirtualKeyFromKey(binding.Key); }
            catch (Exception ex)
            {
                failed.Add(action);
                Log.Warn($"HotkeyService: {action} = {binding} not registered: invalid key ({ex.Message})");
                continue;
            }
            var mods = HotkeyKeys.ToNativeModifiers(binding.Modifiers) | MOD_NOREPEAT;
            if (vk != 0 && RegisterHotKey(hwnd, id, mods, (uint)vk))
            {
                _registered[id] = action;
                continue;
            }
            var err = vk == 0 ? ERROR_INVALID_PARAMETER : Marshal.GetLastPInvokeError();
            if (err == ERROR_HOTKEY_ALREADY_REGISTERED)
            {
                takenByOthers.Add((action, binding));
                Log.Info($"HotkeyService: {action} = {binding} is taken by another program, overriding");
                continue;
            }
            failed.Add(action);
            Log.Warn($"HotkeyService: {action} = {binding} not registered: {DescribeError(err)}");
        }
        _override ??= new HotkeyOverride(action => _dispatcher!.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            try { Pressed?.Invoke(this, action); }
            catch (Exception ex) { Log.Error($"Hotkey handler for {action} failed", ex); }
        }));
        _override.Suspended = false;
        failed.AddRange(_override.Set(takenByOthers));
        _failed = failed.ToArray();
    }

    HotkeyOverride? _override;

    void UnregisterAll()
    {
        if (_override is not null) _override.Suspended = true;
        if (_source is null || _registered.Count == 0) return;
        var hwnd = _source.Handle;
        foreach (var id in _registered.Keys)
            UnregisterHotKey(hwnd, id);
        _registered.Clear();
    }

    nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY || !_registered.TryGetValue((int)wParam, out var action)) return 0;
        handled = true;
        _dispatcher!.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            try { Pressed?.Invoke(this, action); }
            catch (Exception ex) { Log.Error($"Hotkey handler for {action} failed", ex); }
        });
        return 0;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClipBar.Core;
using ClipBar.Hotkeys;

namespace ClipBar.UI.Controls;

public partial class HotkeyRecorderBox : UserControl
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(HotkeyBinding), typeof(HotkeyRecorderBox),
        new FrameworkPropertyMetadata(HotkeyBinding.None, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((HotkeyRecorderBox)d).UpdateVisual(), (_, v) => v ?? HotkeyBinding.None));

    public static readonly DependencyProperty HasConflictProperty = DependencyProperty.Register(
        nameof(HasConflict), typeof(bool), typeof(HotkeyRecorderBox),
        new FrameworkPropertyMetadata(false, (d, _) => ((HotkeyRecorderBox)d).UpdateVisual()));

    static readonly DependencyPropertyKey IsRecordingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsRecording), typeof(bool), typeof(HotkeyRecorderBox), new PropertyMetadata(false));
    public static readonly DependencyProperty IsRecordingProperty = IsRecordingPropertyKey.DependencyProperty;

    public HotkeyBinding Hotkey
    {
        get => (HotkeyBinding)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public bool HasConflict
    {
        get => (bool)GetValue(HasConflictProperty);
        set => SetValue(HasConflictProperty, value);
    }

    public bool IsRecording => (bool)GetValue(IsRecordingProperty);

    public event EventHandler<HotkeyBinding>? HotkeyChanged;

    const string PlaceholderEmpty = "Не назначено";
    const string PlaceholderRecording = "Нажмите сочетание…";
    const string HintNeedModifier = "Нужен Ctrl, Alt, Shift или Win";
    const string TipConflict = "Сочетание занято другой программой";
    const string TipNormal = "Нажмите, чтобы назначить сочетание";

    KeyboardCaptureHook? _hook;
    ModifierKeys _liveMods;
    Key _lastDownKey;
    bool? _prevSuspended;
    bool _focusRing;
    string? _hint;
    DispatcherTimer? _hintTimer;
    DoubleAnimation? _pulse;

    public HotkeyRecorderBox()
    {
        InitializeComponent();
        UpdateVisual();
        Unloaded += (_, _) => CancelRecording();
    }

    public void BeginRecording()
    {
        if (IsRecording) return;
        if (!IsKeyboardFocusWithin) Focus();
        StartRecording();
    }

    public void CancelRecording() => StopRecording();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        _focusRing = false;
        if (IsRecording) return;
        Focus();
        StartRecording();
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); UpdateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); UpdateVisual(); }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _focusRing = InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        UpdateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _focusRing = false;
        if (IsRecording) StopRecording(); else UpdateVisual();
    }

    void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (IsRecording) StopRecording();
            Commit(HotkeyBinding.None);
        }
        catch (Exception ex) { Log.Error("HotkeyRecorderBox clear failed", ex); }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        try
        {
            var key = ActualKey(e);
            if (!IsRecording)
            {
                if (key is Key.Enter or Key.Space && !e.IsRepeat)
                {
                    e.Handled = true;
                    _focusRing = true;
                    StartRecording();
                }
                return;
            }
            e.Handled = true;
            if (_hook?.IsInstalled == true) return;
            if (e.IsRepeat && !HotkeyKeys.IsModifier(key)) return;
            HandleKey(key, true, e.KeyboardDevice.Modifiers);
        }
        catch (Exception ex) { Log.Error("HotkeyRecorderBox key handling failed", ex); }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (!IsRecording) return;
        e.Handled = true;
        if (_hook?.IsInstalled == true) return;
        try
        {
            var key = ActualKey(e);
            if (key == Key.Snapshot) HandleKey(key, true, e.KeyboardDevice.Modifiers);
            else HandleKey(key, false, e.KeyboardDevice.Modifiers);
        }
        catch (Exception ex) { Log.Error("HotkeyRecorderBox key handling failed", ex); }
    }

    static Key ActualKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    bool OnHookKey(Key key, bool isDown, bool injected)
    {
        if (!IsRecording) return false;
        HandleKey(key, isDown, null);
        return true;
    }

    void HandleKey(Key key, bool isDown, ModifierKeys? reportedMods)
    {
        if (key == Key.None) return;

        if (HotkeyKeys.IsModifier(key))
        {
            var m = HotkeyKeys.ModifierOf(key);
            if (isDown) _liveMods |= m; else _liveMods &= ~m;
            if (reportedMods is { } rm) _liveMods = rm;
            UpdateVisual();
            return;
        }

        if (!isDown)
        {
            if (_lastDownKey == key) _lastDownKey = Key.None;
            return;
        }
        if (_lastDownKey == key) return;
        _lastDownKey = key;

        var mods = reportedMods ?? _liveMods;
        if (key == Key.Escape) { StopRecording(); return; }
        if (key is Key.Back or Key.Delete && mods == ModifierKeys.None) { Commit(HotkeyBinding.None); return; }
        if (mods == ModifierKeys.None && !HotkeyKeys.AllowsWithoutModifier(key)) { ShowHint(HintNeedModifier); return; }
        Commit(new HotkeyBinding(mods, key));
    }

    void StartRecording()
    {
        if (IsRecording) return;
        try
        {
            if (AppServices.Hotkeys is { } hk)
            {
                _prevSuspended = hk.Suspended;
                hk.Suspended = true;
            }
        }
        catch (Exception ex) { Log.Error("HotkeyRecorderBox: suspend hotkeys failed", ex); }

        _liveMods = SeedModifiers();
        _lastDownKey = Key.None;
        _hint = null;
        _hook ??= new KeyboardCaptureHook { Handler = OnHookKey };
        _hook.Install();
        SetValue(IsRecordingPropertyKey, true);
        UpdateVisual();
    }

    void StopRecording()
    {
        if (!IsRecording) return;
        _hook?.Uninstall();
        _hintTimer?.Stop();
        _hint = null;
        SetValue(IsRecordingPropertyKey, false);
        try
        {
            if (AppServices.Hotkeys is { } hk && _prevSuspended is { } prev) hk.Suspended = prev;
        }
        catch (Exception ex) { Log.Error("HotkeyRecorderBox: resume hotkeys failed", ex); }
        _prevSuspended = null;
        UpdateVisual();
    }

    void Commit(HotkeyBinding binding)
    {
        StopRecording();
        SetCurrentValue(HasConflictProperty, false);
        if (!Equals(Hotkey, binding)) SetCurrentValue(HotkeyProperty, binding);
        UpdateVisual();
        try { HotkeyChanged?.Invoke(this, binding); }
        catch (Exception ex) { Log.Error("HotkeyChanged handler failed", ex); }
    }

    static ModifierKeys SeedModifiers()
    {
        var m = ModifierKeys.None;
        if (Down(HotkeyNative.VK_CONTROL)) m |= ModifierKeys.Control;
        if (Down(HotkeyNative.VK_MENU)) m |= ModifierKeys.Alt;
        if (Down(HotkeyNative.VK_SHIFT)) m |= ModifierKeys.Shift;
        if (Down(HotkeyNative.VK_LWIN) || Down(HotkeyNative.VK_RWIN)) m |= ModifierKeys.Windows;
        return m;
        static bool Down(int vk) => (HotkeyNative.GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    void ShowHint(string text)
    {
        _hint = text;
        _hintTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(1600), DispatcherPriority.Normal,
            (_, _) => { _hintTimer!.Stop(); _hint = null; UpdateVisual(); }, Dispatcher);
        _hintTimer.Stop();
        _hintTimer.Start();
        UpdateVisual();
    }

    void UpdateVisual()
    {
        if (Root is null) return;
        var recording = IsRecording;
        var conflict = HasConflict && !recording;
        var hotkey = Hotkey;

        ChipsPanel.Children.Clear();
        if (recording)
        {
            if (_hint is null && _liveMods != ModifierKeys.None)
            {
                foreach (var label in HotkeyKeys.ModifierLabels(_liveMods)) AddChip(label, plusBefore: ChipsPanel.Children.Count > 0);
                AddChip("…", plusBefore: true, ghost: true);
            }
        }
        else if (!hotkey.IsEmpty)
        {
            foreach (var label in HotkeyKeys.ModifierLabels(hotkey.Modifiers)) AddChip(label, plusBefore: ChipsPanel.Children.Count > 0);
            AddChip(HotkeyKeys.DisplayName(hotkey.Key), plusBefore: ChipsPanel.Children.Count > 0);
        }

        var showPlaceholder = ChipsPanel.Children.Count == 0;
        Placeholder.Visibility = showPlaceholder ? Visibility.Visible : Visibility.Collapsed;
        if (_hint is not null)
        {
            Placeholder.Text = _hint;
            Placeholder.Foreground = Brush("CB.Warning", "#FFFCE100");
        }
        else if (recording)
        {
            Placeholder.Text = PlaceholderRecording;
            Placeholder.Foreground = Brush("CB.TextSecondary", "#FFC5C5C5");
        }
        else
        {
            Placeholder.Text = PlaceholderEmpty;
            Placeholder.Foreground = Brush("CB.TextTertiary", "#FF8A8A8A");
        }

        EscHint.Visibility = recording && _hint is null ? Visibility.Visible : Visibility.Collapsed;
        ConflictIcon.Visibility = conflict ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = !recording && !hotkey.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

        var hover = IsMouseOver && !recording;
        Root.Background = Brush(hover ? "CB.ControlHover" : "CB.Control", hover ? "#FF454545" : "#FF3A3A3A");
        Root.BorderBrush = recording ? Brush("CB.Accent", "#FF4CC2FF")
                         : conflict ? Brush("CB.Error", "#FFFF99A4")
                         : Brush("CB.WidgetBorder", "#26FFFFFF");
        Underline.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        Underline.Background = Brush("CB.Accent", "#FF4CC2FF");
        FocusRing.Visibility = _focusRing && IsKeyboardFocused && !recording ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = recording ? null : conflict ? TipConflict : TipNormal;
        SetPulse(recording);
    }

    void AddChip(string label, bool plusBefore, bool ghost = false)
    {
        if (plusBefore) AddPlus();
        var shell = new Border { Style = (Style)FindResource(ghost ? "GhostChipShell" : "ChipShell") };
        var face = new Border { Style = (Style)FindResource(ghost ? "GhostChipFace" : "ChipFace") };
        face.Child = new TextBlock { Text = label, Style = (Style)FindResource(ghost ? "GhostChipText" : "ChipText") };
        shell.Child = face;
        ChipsPanel.Children.Add(shell);
    }

    void AddPlus() => ChipsPanel.Children.Add(new TextBlock { Text = "+", Style = (Style)FindResource("ChipPlus") });

    void SetPulse(bool on)
    {
        if (on)
        {
            if (_pulse is not null) return;
            _pulse = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(850))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Glow.BeginAnimation(OpacityProperty, _pulse);
        }
        else if (_pulse is not null)
        {
            Glow.BeginAnimation(OpacityProperty, null);
            Glow.Opacity = 0;
            _pulse = null;
        }
    }

    Brush Brush(string key, string fallbackHex)
    {
        if (TryFindResource(key) is Brush b) return b;
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex));
    }
}

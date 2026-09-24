using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ClipBar.Core;
using Wpf.Ui.Controls;

namespace ClipBar.UI.Overlay.Widgets;

public partial class AudioWidget : UserControl, IOverlayWidget
{
    sealed record DeviceItem(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    public string Id => "audio";
    public string Title => "Звук";
    public SymbolRegular Icon => SymbolRegular.Speaker224;
    public IOverlayHost? Host { get; set; }
    public bool NeedsFastTick => true;

    bool _active;
    bool _suppress;
    int _deviceLoadVersion;
    readonly DispatcherTimer _saveTimer;

    public AudioWidget()
    {
        InitializeComponent();
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };

        SysSlider.ValueChanged += (_, e) => OnVolumeChanged(isMic: false, e.NewValue);
        MicSlider.ValueChanged += (_, e) => OnVolumeChanged(isMic: true, e.NewValue);
        SysSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => FlushSave()));
        MicSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => FlushSave()));
        OutputCombo.SelectionChanged += (_, _) => OnDeviceChanged(isMic: false);
        MicCombo.SelectionChanged += (_, _) => OnDeviceChanged(isMic: true);
    }

    static IAudioHub? Audio => AppServices.Audio;
    static SettingsService? SettingsSvc => AppServices.Settings;

    public void Activate()
    {
        if (_active) return;
        _active = true;
        if (Audio is { } a) a.Changed += OnAudioChanged;
        if (SettingsSvc is { } s) s.Changed += OnSettingsChanged;
        RefreshFromHub();
        LoadDevicesAsync();
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        if (Audio is { } a) a.Changed -= OnAudioChanged;
        if (SettingsSvc is { } s) s.Changed -= OnSettingsChanged;
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
        SysMeter.Level = 0; MicMeter.Level = 0;
    }

    public void FastTick()
    {
        var a = Audio;
        if (a is null) return;
        SysMeter.Level = a.SystemLevel;
        MicMeter.Level = a.MicLevel;
    }

    public void SlowTick()
    {
        if (HintText.Visibility == Visibility.Visible && AppServices.Engine is { IsRecording: false })
            HintText.Visibility = Visibility.Collapsed;
    }

    void OnAudioChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshFromHub);
    void OnSettingsChanged(object? sender, AppSettings e) => Dispatcher.BeginInvoke(() => { RefreshFromHub(); SelectSavedDevices(); });

    void RefreshFromHub()
    {
        if (!_active) return;
        var a = Audio;
        if (a is null) return;
        _suppress = true;
        try
        {
            SysSlider.Value = Math.Clamp(a.SystemVolume * 100, 0, 200);
            MicSlider.Value = Math.Clamp(a.MicVolume * 100, 0, 200);
            SysPercent.Text = $"{Math.Round(SysSlider.Value)}%";
            MicPercent.Text = $"{Math.Round(MicSlider.Value)}%";
            var muted = a.MicMuted;
            MuteToggle.IsChecked = muted;
            MuteIcon.Symbol = muted ? SymbolRegular.MicOff20 : SymbolRegular.Mic20;
            MuteToggle.ToolTip = (muted ? "Включить микрофон" : "Выключить микрофон") + $" ({Actions.HotkeyText(HotkeyAction.ToggleMic)})";
            MicMeter.IsMuted = muted;
            MicIcon.Symbol = muted ? SymbolRegular.MicOff24 : SymbolRegular.Mic24;

            var s = SettingsSvc?.Current;
            var micEnabled = s?.RecordMic ?? true;
            var sysEnabled = s?.RecordSystemAudio ?? true;
            MicSlider.IsEnabled = micEnabled; MicCombo.IsEnabled = micEnabled; MuteToggle.IsEnabled = micEnabled;
            SysSlider.IsEnabled = sysEnabled; OutputCombo.IsEnabled = sysEnabled;
            SysSlider.Opacity = sysEnabled ? 1 : 0.5;
            MicSlider.Opacity = micEnabled ? 1 : 0.5;
        }
        catch (Exception ex) { Log.Error("AudioWidget refresh failed", ex); }
        finally { _suppress = false; }
    }

    void LoadDevicesAsync()
    {
        var a = Audio;
        if (a is null) return;
        var version = ++_deviceLoadVersion;
        _ = Task.Run(() =>
        {
            IReadOnlyList<AudioDeviceInfo> outs = [], ins = [];
            try { outs = a.GetOutputDevices(); } catch (Exception ex) { Log.Error("GetOutputDevices failed", ex); }
            try { ins = a.GetInputDevices(); } catch (Exception ex) { Log.Error("GetInputDevices failed", ex); }
            Dispatcher.BeginInvoke(() =>
            {
                if (version != _deviceLoadVersion || !_active) return;
                Fill(OutputCombo, outs);
                Fill(MicCombo, ins);
                SelectSavedDevices();
            });
        });
    }

    void Fill(ComboBox combo, IReadOnlyList<AudioDeviceInfo> devices)
    {
        _suppress = true;
        try
        {
            combo.Items.Clear();
            var def = devices.FirstOrDefault(d => d.IsDefault);
            combo.Items.Add(new DeviceItem(null, def is null ? "По умолчанию" : $"По умолчанию ({def.Name})"));
            foreach (var d in devices) combo.Items.Add(new DeviceItem(d.Id, d.Name));
            combo.SelectedIndex = 0;
        }
        finally { _suppress = false; }
    }

    void SelectSavedDevices()
    {
        var s = SettingsSvc?.Current;
        if (s is null) return;
        _suppress = true;
        try
        {
            Select(OutputCombo, s.SystemDeviceId);
            Select(MicCombo, s.MicDeviceId);
        }
        finally { _suppress = false; }

        static void Select(ComboBox combo, string? id)
        {
            if (combo.Items.Count == 0) return;
            foreach (var item in combo.Items)
                if (item is DeviceItem d && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) { combo.SelectedItem = item; return; }
            combo.SelectedIndex = 0;
        }
    }

    void OnDeviceChanged(bool isMic)
    {
        if (_suppress || !_active) return;
        var combo = isMic ? MicCombo : OutputCombo;
        if (combo.SelectedItem is not DeviceItem item) return;
        var svc = SettingsSvc;
        if (svc is null) return;
        try
        {
            if (isMic) svc.Current.MicDeviceId = item.Id; else svc.Current.SystemDeviceId = item.Id;
            svc.Save();
            _ = ApplyToEngineAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Audio device change failed", ex);
            AppServices.Notifier?.Show("Не удалось сменить устройство", ex.Message, NotifyKind.Error);
        }
    }

    async Task ApplyToEngineAsync()
    {
        try
        {
            var a = Audio;
            if (a is not null) { try { a.SetMonitoring(false); a.SetMonitoring(true); } catch (Exception ex) { Log.Error("SetMonitoring failed", ex); } }

            var engine = AppServices.Engine;
            if (engine is null) return;
            var ok = await engine.RestartAsync();
            HintText.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (!ok) AppServices.Notifier?.Show("Идёт запись", "Новое устройство будет использовано после остановки записи", NotifyKind.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("Engine restart after device change failed", ex);
            AppServices.Notifier?.Show("Не удалось перезапустить захват", ex.Message, NotifyKind.Error);
        }
    }

    void OnVolumeChanged(bool isMic, double value)
    {
        if (isMic) MicPercent.Text = $"{Math.Round(value)}%"; else SysPercent.Text = $"{Math.Round(value)}%";
        if (_suppress || !_active) return;
        var a = Audio;
        if (a is null) return;
        try
        {
            var gain = (float)(value / 100.0);
            if (isMic) a.MicVolume = gain; else a.SystemVolume = gain;
            _saveTimer.Stop(); _saveTimer.Start();
        }
        catch (Exception ex) { Log.Error("Volume change failed", ex); }
    }

    void FlushSave()
    {
        if (!_saveTimer.IsEnabled) return;
        _saveTimer.Stop();
        SaveSettings();
    }

    static void SaveSettings()
    {
        try { SettingsSvc?.Save(); } catch (Exception ex) { Log.Error("Settings save failed", ex); }
    }

    void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var a = Audio;
        if (a is null) return;
        try
        {
            if (a.MicMuted != (MuteToggle.IsChecked == true)) Actions.ToggleMic();
            RefreshFromHub();
        }
        catch (Exception ex) { Log.Error("ToggleMic failed", ex); }
    }
}

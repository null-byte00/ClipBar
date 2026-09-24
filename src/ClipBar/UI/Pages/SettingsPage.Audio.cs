using System.Windows;
using System.Windows.Controls;
using ClipBar.Core;
using Wpf.Ui.Controls;

namespace ClipBar.UI.Pages;

public partial class SettingsPage
{
    const string DefaultDevice = "";
    bool _devicesLoaded;

    void FillAudioCombos()
    {
        foreach (var kbps in new[] { 128, 160, 192, 256, 320 })
            AudioBitrateCombo.Items.Add(new Choice<int>($"{kbps} кбит/с", kbps));
        SystemDeviceCombo.Items.Add(new Choice<string>("По умолчанию", DefaultDevice));
        MicDeviceCombo.Items.Add(new Choice<string>("По умолчанию", DefaultDevice));
    }

    void LoadAudio()
    {
        var s = S;
        SystemAudioToggle.IsChecked = s.RecordSystemAudio;
        MicToggle.IsChecked = s.RecordMic;
        SystemAudioPanel.IsEnabled = s.RecordSystemAudio;
        MicPanel.IsEnabled = s.RecordMic;
        MicHeader.Description = $"Твой голос отдельной дорожкой. Заглушить на лету — {Actions.HotkeyText(HotkeyAction.ToggleMic)}";
        SelectDevice(SystemDeviceCombo, s.SystemDeviceId);
        SelectDevice(MicDeviceCombo, s.MicDeviceId);
        LoadVolumes();
        TracksToggle.IsChecked = s.SeparateAudioTracks;
        Select<int>(AudioBitrateCombo, k => k == s.AudioBitrateKbps, $"{s.AudioBitrateKbps} кбит/с", s.AudioBitrateKbps);
        AudioOffsetBox.Value = s.AudioOffsetMs;
    }

    void LoadVolumes()
    {
        var s = S;
        var audio = AppServices.Audio;
        var sys = audio?.SystemVolume ?? s.SystemVolume;
        var mic = audio?.MicVolume ?? s.MicVolume;
        SystemVolumeSlider.Value = Math.Round(Math.Clamp(sys, 0f, 2f) * 100);
        MicVolumeSlider.Value = Math.Round(Math.Clamp(mic, 0f, 2f) * 100);
        SystemVolumeText.Text = $"{SystemVolumeSlider.Value:0} %";
        MicVolumeText.Text = $"{MicVolumeSlider.Value:0} %";
    }

    void SelectDevice(ComboBox combo, string? id)
    {
        var want = id ?? DefaultDevice;
        var missing = _devicesLoaded ? "Устройство не подключено" : "Сохранённое устройство";
        Select<string>(combo, d => d == want, missing, want);
    }

    async Task LoadAudioDevicesAsync()
    {
        if (_devicesLoaded || AppServices.Audio is not { } audio) return;
        try
        {
            var (outputs, inputs) = await Task.Run(() =>
            {
                IReadOnlyList<AudioDeviceInfo> o = [], i = [];
                try { o = audio.GetOutputDevices(); } catch (Exception ex) { Log.Error("GetOutputDevices failed", ex); }
                try { i = audio.GetInputDevices(); } catch (Exception ex) { Log.Error("GetInputDevices failed", ex); }
                return (o, i);
            });
            Guarded(() =>
            {
                Fill(SystemDeviceCombo, outputs);
                Fill(MicDeviceCombo, inputs);
                _devicesLoaded = true;
                SelectDevice(SystemDeviceCombo, S.SystemDeviceId);
                SelectDevice(MicDeviceCombo, S.MicDeviceId);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Audio device list failed", ex);
        }

        static void Fill(ComboBox combo, IReadOnlyList<AudioDeviceInfo> devices)
        {
            combo.Items.Clear();
            combo.Items.Add(new Choice<string>("По умолчанию", DefaultDevice));
            foreach (var d in devices)
                combo.Items.Add(new Choice<string>(d.IsDefault ? $"{d.Name} (по умолчанию)" : d.Name, d.Id));
        }
    }

    void OnSystemAudioToggleClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.RecordSystemAudio = SystemAudioToggle.IsChecked == true;
        SystemAudioPanel.IsEnabled = S.RecordSystemAudio;
        SaveNeedsRestart();
        UpdateEstimate();
    }

    void OnMicToggleClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.RecordMic = MicToggle.IsChecked == true;
        MicPanel.IsEnabled = S.RecordMic;
        SaveNeedsRestart();
        UpdateEstimate();
    }

    void OnSystemDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SystemDeviceCombo.SelectedItem is not Choice<string> c) return;
        S.SystemDeviceId = c.Value == DefaultDevice ? null : c.Value;
        SaveNeedsRestart();
    }

    void OnMicDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MicDeviceCombo.SelectedItem is not Choice<string> c) return;
        S.MicDeviceId = c.Value == DefaultDevice ? null : c.Value;
        SaveNeedsRestart();
    }

    void OnSystemVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SystemVolumeText.Text = $"{e.NewValue:0} %";
        if (_loading) return;
        var gain = (float)(e.NewValue / 100.0);
        S.SystemVolume = gain;
        if (AppServices.Audio is { } audio) audio.SystemVolume = gain;
        SaveDebounced();
    }

    void OnMicVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        MicVolumeText.Text = $"{e.NewValue:0} %";
        if (_loading) return;
        var gain = (float)(e.NewValue / 100.0);
        S.MicVolume = gain;
        if (AppServices.Audio is { } audio) audio.MicVolume = gain;
        SaveDebounced();
    }

    void OnTracksToggleClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.SeparateAudioTracks = TracksToggle.IsChecked == true;
        SaveNeedsRestart();
        UpdateEstimate();
    }

    void OnAudioBitrateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AudioBitrateCombo.SelectedItem is not Choice<int> c) return;
        S.AudioBitrateKbps = c.Value;
        SaveNeedsRestart();
        UpdateEstimate();
    }

    void OnAudioOffsetChanged(object sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || args.NewValue is not { } ms) return;
        var value = (int)Math.Round(Math.Clamp(ms, -2000, 2000));
        if (S.AudioOffsetMs == value) return;
        S.AudioOffsetMs = value;
        SaveNeedsRestart();
    }
}

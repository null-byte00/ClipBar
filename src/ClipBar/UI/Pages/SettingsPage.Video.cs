using System.Windows;
using System.Windows.Controls;
using ClipBar.Core;
using Wpf.Ui.Controls;

namespace ClipBar.UI.Pages;

public partial class SettingsPage
{
    static readonly (string Label, int Kbps)[] QualityPresets =
    [
        ("Низкое · 8 Мбит/с", 8000),
        ("Среднее · 15 Мбит/с", 15000),
        ("Высокое · 20 Мбит/с", 20000),
        ("Ультра · 40 Мбит/с", 40000),
    ];
    const int CustomQuality = -1;
    bool _encodersLoaded;

    void FillVideoCombos()
    {
        foreach (var (label, kbps) in QualityPresets) QualityCombo.Items.Add(new Choice<int>(label, kbps));
        QualityCombo.Items.Add(new Choice<int>("Своё", CustomQuality));

        foreach (var fps in new[] { 30, 60, 120, 144 }) FpsCombo.Items.Add(new Choice<int>($"{fps} к/с", fps));

        ResolutionCombo.Items.Add(new Choice<int>("Исходное", 0));
        ResolutionCombo.Items.Add(new Choice<int>("1440p · QHD", 1440));
        ResolutionCombo.Items.Add(new Choice<int>("1080p · Full HD", 1080));
        ResolutionCombo.Items.Add(new Choice<int>("720p · HD", 720));

        EncoderCombo.Items.Add(new Choice<string>("Авто (рекомендуется)", "auto"));
    }

    void LoadVideo()
    {
        var s = S;

        if (MonitorCombo.Items.Count == 0)
        {
            var monitors = SettingsNative.GetMonitors();
            if (monitors.Count == 0) monitors = [new SettingsNative.MonitorInfo(0, 0, 0, true, "")];
            foreach (var m in monitors)
            {
                var size = m.Width > 0 ? $" · {m.Width}×{m.Height}" : "";
                var primary = m.IsPrimary ? " · основной" : "";
                MonitorCombo.Items.Add(new Choice<int>($"Монитор {m.Index + 1}{size}{primary}", m.Index));
            }
        }
        Select<int>(MonitorCombo, i => i == s.MonitorIndex, $"Монитор {s.MonitorIndex + 1} (не найден)", s.MonitorIndex);

        var isPreset = QualityPresets.Any(p => p.Kbps == s.VideoBitrateKbps);
        Select<int>(QualityCombo, k => isPreset ? k == s.VideoBitrateKbps : k == CustomQuality);
        VideoBitrateBox.Value = Math.Round(s.VideoBitrateKbps / 1000.0);
        VideoBitratePanel.Visibility = isPreset ? Visibility.Collapsed : Visibility.Visible;

        Select<int>(FpsCombo, f => f == s.Fps, $"{s.Fps} к/с", s.Fps);
        Select<int>(ResolutionCombo, h => h == s.OutputHeight, $"{s.OutputHeight}p", s.OutputHeight);
        SelectEncoder(s.Encoder);
        CursorToggle.IsChecked = s.CaptureCursor;
    }

    void SelectEncoder(string id)
    {
        var missing = _encodersLoaded ? $"{id} (недоступен на этом ПК)" : id;
        Select<string>(EncoderCombo, e => string.Equals(e, id, StringComparison.OrdinalIgnoreCase), missing, id);
    }

    async Task LoadEncodersAsync()
    {
        if (_encodersLoaded || Engine is not { } eng) return;
        EncoderSpinner.Visibility = Visibility.Visible;
        EncoderCombo.IsEnabled = false;
        try
        {
            var encoders = await eng.GetAvailableEncodersAsync();
            if (!IsLoaded && !_subscribed) return;
            Guarded(() =>
            {
                EncoderCombo.Items.Clear();
                EncoderCombo.Items.Add(new Choice<string>("Авто (рекомендуется)", "auto"));
                foreach (var enc in encoders)
                    EncoderCombo.Items.Add(new Choice<string>($"{enc.DisplayName} · {(enc.Hardware ? "видеокарта" : "процессор")}", enc.Id));
                _encodersLoaded = true;
                SelectEncoder(S.Encoder);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Encoder probe failed", ex);
        }
        finally
        {
            EncoderSpinner.Visibility = Visibility.Collapsed;
            EncoderCombo.IsEnabled = true;
        }
    }

    void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MonitorCombo.SelectedItem is not Choice<int> c) return;
        S.MonitorIndex = c.Value;
        SaveNeedsRestart();
    }

    void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || QualityCombo.SelectedItem is not Choice<int> c) return;
        if (c.Value == CustomQuality)
        {
            VideoBitratePanel.Visibility = Visibility.Visible;
            VideoBitrateBox.Focus();
            return;
        }
        VideoBitratePanel.Visibility = Visibility.Collapsed;
        S.VideoBitrateKbps = c.Value;
        SaveNeedsRestart();
        UpdateEstimate();
    }

    void OnVideoBitrateChanged(object sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || args.NewValue is not { } mbit) return;
        var kbps = (int)Math.Round(Math.Clamp(mbit, 1, 200) * 1000);
        if (S.VideoBitrateKbps == kbps) return;
        S.VideoBitrateKbps = kbps;
        SaveNeedsRestart();
        UpdateEstimate();
    }

    void OnFpsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FpsCombo.SelectedItem is not Choice<int> c) return;
        S.Fps = c.Value;
        SaveNeedsRestart();
    }

    void OnResolutionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ResolutionCombo.SelectedItem is not Choice<int> c) return;
        S.OutputHeight = c.Value;
        SaveNeedsRestart();
    }

    void OnEncoderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EncoderCombo.SelectedItem is not Choice<string> c) return;
        S.Encoder = c.Value;
        SaveNeedsRestart();
    }

    void OnCursorToggleClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.CaptureCursor = CursorToggle.IsChecked == true;
        SaveNeedsRestart();
    }
}

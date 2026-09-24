using System.Windows;
using System.Windows.Controls;
using ClipBar.Core;
using Wpf.Ui.Controls;

namespace ClipBar.UI.Pages;

public partial class SettingsPage
{
    static readonly int[] ReplayPresets = [15, 30, 60, 120, 300, 600, 1200, 1800];
    readonly List<RadioButton> _replayChips = [];
    RadioButton? _customChip;

    void BuildReplayChips()
    {
        var style = (Style)FindResource("Chip");
        foreach (var seconds in ReplayPresets)
        {
            var chip = new RadioButton
            {
                Style = style,
                GroupName = "ReplayLength",
                Content = seconds < 60 ? $"{seconds} с" : $"{seconds / 60} мин",
                Tag = seconds,
            };
            chip.Checked += OnReplayChipChecked;
            _replayChips.Add(chip);
            ReplayChips.Children.Add(chip);
        }
        _customChip = new RadioButton { Style = style, GroupName = "ReplayLength", Content = "Своё", Tag = -1 };
        _customChip.Checked += OnReplayChipChecked;
        ReplayChips.Children.Add(_customChip);
    }

    void LoadReplay()
    {
        var s = S;
        var eng = Engine;
        ReplayToggle.IsChecked = eng is not null ? eng.ReplayActive || eng.State == EngineState.Starting && s.ReplayEnabled : s.ReplayEnabled;
        ReplayHeader.Description = $"Последние секунды экрана всегда под рукой — сохрани их по {Actions.HotkeyText(HotkeyAction.SaveReplay)}";

        var preset = _replayChips.FirstOrDefault(c => (int)c.Tag == s.ReplaySeconds);
        if (preset is not null)
        {
            preset.IsChecked = true;
            ReplayCustomPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            _customChip!.IsChecked = true;
            ReplayCustomPanel.Visibility = Visibility.Visible;
        }
        ReplayCustomBox.Value = Math.Round(s.ReplaySeconds / 60.0, 2);
        ReplayLengthValue.Text = Actions.FormatDuration(TimeSpan.FromSeconds(s.ReplaySeconds));
    }

    async void OnReplayToggleClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = ReplayToggle.IsChecked == true;
        S.ReplayEnabled = on;
        Save();
        var eng = Engine;
        if (eng is null) return;
        ReplayToggle.IsEnabled = false;
        try
        {
            if (on) await eng.StartReplayAsync();
            else await eng.StopReplayAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Replay toggle failed", ex);
            AppServices.Notifier?.Show("Ошибка буфера отката", ex.Message, NotifyKind.Error);
            Guarded(() => ReplayToggle.IsChecked = eng.ReplayActive);
        }
        finally { ReplayToggle.IsEnabled = true; }
    }

    void OnReplayChipChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton chip) return;
        var seconds = (int)chip.Tag;
        if (seconds < 0)
        {
            ReplayCustomPanel.Visibility = Visibility.Visible;
            ReplayCustomBox.Focus();
            return;
        }
        ReplayCustomPanel.Visibility = Visibility.Collapsed;
        ApplyReplaySeconds(seconds);
    }

    void OnReplayCustomChanged(object sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || args.NewValue is not { } minutes) return;
        var seconds = (int)Math.Round(Math.Clamp(minutes * 60, 15, 3600));
        ApplyReplaySeconds(seconds, debounce: true);
    }

    void ApplyReplaySeconds(int seconds, bool debounce = false)
    {
        seconds = Math.Clamp(seconds, 15, 3600);
        if (S.ReplaySeconds != seconds)
        {
            S.ReplaySeconds = seconds;
            if (debounce) SaveDebounced(); else Save();
        }
        ReplayLengthValue.Text = Actions.FormatDuration(TimeSpan.FromSeconds(seconds));
        UpdateEstimate();
    }
}

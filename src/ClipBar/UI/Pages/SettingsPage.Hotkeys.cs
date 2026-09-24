using System.Windows;
using ClipBar.Core;
using ClipBar.UI.Controls;
using Wpf.Ui.Controls;

namespace ClipBar.UI.Pages;

public partial class SettingsPage
{
    sealed record HotkeyRow(HotkeyAction Action, HotkeyRecorderBox Box, SettingsRowHeader Header);
    readonly List<HotkeyRow> _hotkeyRows = [];

    void BuildHotkeyRows()
    {
        var rowStyle = (Style)FindResource("Row");
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var box = new HotkeyRecorderBox { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
            var header = new SettingsRowHeader { Title = action.Title(), Description = HotkeyDescription(action) };
            var card = new CardControl
            {
                Style = rowStyle,
                Icon = new SymbolIcon { Symbol = HotkeyIcon(action) },
                Header = header,
                Content = box,
            };
            box.HotkeyChanged += (_, binding) => OnHotkeyChanged(action, binding);
            _hotkeyRows.Add(new HotkeyRow(action, box, header));
            HotkeyRows.Children.Add(card);
        }
    }

    static SymbolRegular HotkeyIcon(HotkeyAction a) => a switch
    {
        HotkeyAction.SaveReplay => SymbolRegular.History24,
        HotkeyAction.ToggleRecording => SymbolRegular.Record24,
        HotkeyAction.Screenshot => SymbolRegular.Camera24,
        HotkeyAction.RegionScreenshot or HotkeyAction.RegionScreenshotKey => SymbolRegular.ScreenshotRecord24,
        HotkeyAction.ToggleMic => SymbolRegular.Mic24,
        HotkeyAction.ToggleOverlay => SymbolRegular.XboxController24,
        HotkeyAction.ToggleReplayBuffer => SymbolRegular.Timer24,
        _ => SymbolRegular.Keyboard24,
    };

    static string HotkeyDescription(HotkeyAction a) => a switch
    {
        HotkeyAction.SaveReplay => "Мгновенно сохранить последние секунды из буфера в клип",
        HotkeyAction.ToggleRecording => "Начать обычную запись или остановить и сохранить её",
        HotkeyAction.Screenshot => "PNG всего экрана в папку клипов",
        HotkeyAction.RegionScreenshot => "Выдели область мышкой — копировать или сохранить, как в Lightshot",
        HotkeyAction.RegionScreenshotKey => "То же самое на второй клавише — по умолчанию PrintScreen",
        HotkeyAction.ToggleMic => "Быстро заглушить или включить микрофон в записи",
        HotkeyAction.ToggleOverlay => "Панель с виджетами поверх игры, как Win+G",
        HotkeyAction.ToggleReplayBuffer => "Включить или выключить фоновый буфер отката",
        _ => "",
    };

    void LoadHotkeys()
    {
        var all = ParsedHotkeys();
        foreach (var row in _hotkeyRows)
            row.Box.Hotkey = all.GetValueOrDefault(row.Action, HotkeyBinding.None);
        RefreshHotkeyConflicts(all, AppServices.Hotkeys?.FailedActions ?? []);
    }

    Dictionary<HotkeyAction, HotkeyBinding> ParsedHotkeys()
    {
        var result = new Dictionary<HotkeyAction, HotkeyBinding>();
        foreach (var action in Enum.GetValues<HotkeyAction>())
            result[action] = HotkeyBinding.Parse(S.Hotkeys.GetValueOrDefault(action));
        return result;
    }

    internal void OnHotkeyChanged(HotkeyAction action, HotkeyBinding binding)
    {
        if (_loading) return;
        try
        {
            S.Hotkeys[action] = binding?.ToString() ?? "";
            Save();
            ApplyHotkeys();
            Guarded(() =>
            {
                ReplayHeader.Description = $"Последние секунды экрана всегда под рукой — сохрани их по {Actions.HotkeyText(HotkeyAction.SaveReplay)}";
                MicHeader.Description = $"Твой голос отдельной дорожкой. Заглушить на лету — {Actions.HotkeyText(HotkeyAction.ToggleMic)}";
            });
        }
        catch (Exception ex)
        {
            Log.Error("Hotkey change failed", ex);
            AppServices.Notifier?.Show("Не удалось назначить клавишу", ex.Message, NotifyKind.Error);
        }
    }

    void ApplyHotkeys()
    {
        var all = ParsedHotkeys();
        IReadOnlyList<HotkeyAction> failed = [];
        try { failed = AppServices.Hotkeys?.Apply(all) ?? []; }
        catch (Exception ex) { Log.Error("Hotkeys.Apply failed", ex); }
        RefreshHotkeyConflicts(all, failed);
    }

    void RefreshHotkeyConflicts(Dictionary<HotkeyAction, HotkeyBinding> all, IReadOnlyList<HotkeyAction> failed)
    {
        foreach (var row in _hotkeyRows)
        {
            var binding = all.GetValueOrDefault(row.Action, HotkeyBinding.None);
            string? warning = null;
            if (!binding.IsEmpty)
            {
                var duplicate = all.FirstOrDefault(kv => kv.Key != row.Action && !kv.Value.IsEmpty && kv.Value == binding);
                if (!duplicate.Value?.IsEmpty ?? false)
                    warning = $"Это сочетание уже назначено на «{duplicate.Key.Title()}»";
                else if (failed.Contains(row.Action))
                    warning = "Сочетание занято другим приложением — выбери другое";
            }
            row.Box.HasConflict = warning is not null;
            row.Header.Warning = warning ?? "";
        }
    }

    void OnResetHotkeysClick(object sender, RoutedEventArgs e)
    {
        try
        {
            S.Hotkeys = AppSettings.DefaultHotkeys();
            Save();
            Guarded(LoadHotkeys);
            ApplyHotkeys();
            Guarded(() =>
            {
                ReplayHeader.Description = $"Последние секунды экрана всегда под рукой — сохрани их по {Actions.HotkeyText(HotkeyAction.SaveReplay)}";
                MicHeader.Description = $"Твой голос отдельной дорожкой. Заглушить на лету — {Actions.HotkeyText(HotkeyAction.ToggleMic)}";
            });
            AppServices.Notifier?.Show("Горячие клавиши сброшены", "Alt+F10 — откат, Alt+F9 — запись, Alt+F1 — скриншот", NotifyKind.Info);
        }
        catch (Exception ex)
        {
            Log.Error("Reset hotkeys failed", ex);
            AppServices.Notifier?.Show("Не удалось сбросить клавиши", ex.Message, NotifyKind.Error);
        }
    }
}

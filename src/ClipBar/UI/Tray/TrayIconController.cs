using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClipBar.Core;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Wpf.Ui.Controls;
using Icon = System.Drawing.Icon;
using MenuItem = Wpf.Ui.Controls.MenuItem;

namespace ClipBar.UI.Tray;

public sealed class TrayIconController : IDisposable
{
    readonly TaskbarIcon _icon;
    readonly Icon _normalIcon, _recordingIcon;
    readonly DispatcherTimer _recordingTick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly MenuItem _recordItem, _replayItem, _saveItem, _shotItem;
    readonly Dispatcher _ui;
    bool _showingRecordingIcon, _disposed;

    public TrayIconController()
    {
        _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _normalIcon = LoadIcon("clipbar.ico");
        _recordingIcon = LoadIcon("clipbar-rec.ico");

        _saveItem = Item("Сохранить откат", SymbolRegular.ArrowCounterclockwise24, () => _ = Actions.SaveReplayAsync());
        _recordItem = Item("Начать запись", SymbolRegular.Record24, () => _ = Actions.ToggleRecordingAsync());
        _shotItem = Item("Скриншот", SymbolRegular.Camera24, () => _ = Actions.ScreenshotAsync());
        _replayItem = Item("Откат включён", SymbolRegular.History24, () => _ = Actions.ToggleReplayBufferAsync());
        _replayItem.IsCheckable = true;

        var open = Item("Открыть ClipBar", SymbolRegular.WindowNew20, () => AppServices.Shell.ShowOverlay());
        open.FontWeight = FontWeights.SemiBold;

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(_saveItem);
        menu.Items.Add(_recordItem);
        menu.Items.Add(_shotItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_replayItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Коллекция", SymbolRegular.ImageMultiple24, () => AppServices.Shell.ShowOverlay(gallery: true)));
        menu.Items.Add(Item("Главное окно", SymbolRegular.Window20, () => AppServices.Shell.ShowMain()));
        menu.Items.Add(Item("Настройки", SymbolRegular.Settings24, () => AppServices.Shell.ShowMain("settings")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Выход", SymbolRegular.ArrowExit20, () => AppServices.Shell.ExitApp()));
        menu.Opened += (_, _) => Refresh();

        _icon = new TaskbarIcon
        {
            Icon = (Icon)_normalIcon.Clone(),
            ToolTipText = "ClipBar",
            NoLeftClickDelay = true,
            MenuActivation = PopupActivationMode.RightClick,
            ContextMenu = menu,
        };
        _icon.TrayLeftMouseUp += (_, _) => Safe(() => AppServices.Shell.ShowOverlay());
        _icon.TrayMouseDoubleClick += (_, _) => Safe(() => AppServices.Shell.ShowOverlay());
        _icon.ForceCreate(false);

        _recordingTick.Tick += (_, _) => Refresh();
        if (AppServices.Engine is { } engine) engine.StateChanged += OnStateChanged;
        if (AppServices.Settings is { } settings) settings.Changed += OnSettingsChanged;
        Refresh();
    }

    void OnStateChanged(object? sender, EventArgs e) => _ui.BeginInvoke(Refresh);
    void OnSettingsChanged(object? sender, AppSettings e) => _ui.BeginInvoke(Refresh);

    void Refresh()
    {
        if (_disposed) return;
        try
        {
            var engine = AppServices.Engine;
            var settings = AppServices.Settings?.Current;
            var recording = engine?.IsRecording == true;

            _icon.ToolTipText = engine switch
            {
                null => "ClipBar",
                { IsRecording: true } => $"ClipBar — идёт запись {engine.RecordingElapsed:mm\\:ss}",
                { State: EngineState.Error } => "ClipBar — ошибка захвата",
                { State: EngineState.Starting } => "ClipBar — запуск захвата…",
                { ReplayActive: true, State: EngineState.Buffering } =>
                    $"ClipBar — откат {Actions.FormatDuration(TimeSpan.FromSeconds(settings?.ReplaySeconds ?? 300))}",
                _ => "ClipBar — откат выключен",
            };

            if (recording != _showingRecordingIcon)
            {
                _showingRecordingIcon = recording;
                var old = _icon.Icon;
                _icon.Icon = (Icon)(recording ? _recordingIcon : _normalIcon).Clone();
                old?.Dispose();   // прошлый клон освобождаем, иначе течёт GDI-хендл на каждый старт/стоп записи
            }
            if (recording && !_recordingTick.IsEnabled) _recordingTick.Start();
            else if (!recording && _recordingTick.IsEnabled) _recordingTick.Stop();

            _recordItem.Header = recording ? "Остановить запись" : "Начать запись";
            _recordItem.Icon = new SymbolIcon { Symbol = recording ? SymbolRegular.RecordStop24 : SymbolRegular.Record24 };
            _recordItem.InputGestureText = Actions.HotkeyText(HotkeyAction.ToggleRecording);
            _saveItem.InputGestureText = Actions.HotkeyText(HotkeyAction.SaveReplay);
            _saveItem.IsEnabled = engine?.ReplayActive == true;
            _shotItem.InputGestureText = Actions.HotkeyText(HotkeyAction.Screenshot);
            _replayItem.IsChecked = engine?.ReplayActive == true;
            _replayItem.Header = engine?.ReplayActive == true ? "Откат включён" : "Откат выключен";
            _replayItem.InputGestureText = Actions.HotkeyText(HotkeyAction.ToggleReplayBuffer);
        }
        catch (Exception ex) { Log.Error("Tray refresh failed", ex); }
    }

    static MenuItem Item(string header, SymbolRegular symbol, Action onClick)
    {
        var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = symbol } };
        item.Click += (_, _) => Safe(onClick);
        return item;
    }

    static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log.Error("Tray action failed", ex);
            AppServices.Notifier?.Show("Ошибка", ex.Message, NotifyKind.Error);
        }
    }

    static Icon LoadIcon(string fileName)
    {
        var res = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{fileName}"))
                  ?? throw new InvalidOperationException($"Resource Assets/{fileName} not found");
        using var stream = res.Stream;
        var size = GetSystemMetrics(SM_CXSMICON);
        if (size <= 0) size = 16;
        return new Icon(stream, new System.Drawing.Size(size, size));
    }

    const int SM_CXSMICON = 49;
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recordingTick.Stop();
        if (AppServices.Engine is { } engine) engine.StateChanged -= OnStateChanged;
        if (AppServices.Settings is { } settings) settings.Changed -= OnSettingsChanged;
        try { _icon.Dispose(); } catch (Exception ex) { Log.Error("Tray icon dispose failed", ex); }
        _normalIcon.Dispose();
        _recordingIcon.Dispose();
    }
}

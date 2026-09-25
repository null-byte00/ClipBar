using System.Windows;
using System.Windows.Threading;
using ClipBar.Core;

namespace ClipBar.UI.Notifications;

public sealed class ToastNotifier : INotifier, IDisposable
{
    readonly Dispatcher _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    ToastHostWindow? _host;
    DateTime _lastRecordingStop = DateTime.MinValue;
    bool _wasRecording, _disposed;
    readonly object _gate = new();
    string? _lastKey;
    DateTime _lastKeyAt, _lastSoundAt;

    public ToastNotifier()
    {
        if (AppServices.Engine is { } engine)
        {
            _wasRecording = engine.IsRecording;
            engine.StateChanged += OnEngineStateChanged;
        }
    }

    void OnEngineStateChanged(object? sender, EventArgs e)
    {
        var recording = AppServices.Engine?.IsRecording == true;
        if (_wasRecording && !recording) _lastRecordingStop = DateTime.UtcNow;
        _wasRecording = recording;
    }

    public void ShowAction(string title, string message, NotifyKind kind, string hint, Action onClick) =>
        ShowInternal(title, message, kind, null, hint, onClick);

    public void Show(string title, string message, NotifyKind kind = NotifyKind.Info, string? clipPath = null) =>
        ShowInternal(title, message, kind, clipPath, null, null);

    void ShowInternal(string title, string message, NotifyKind kind, string? clipPath, string? hint, Action? onClick)
    {
        if (_disposed) return;
        Log.Info($"[toast/{kind}] {title}: {message}");
        var settings = AppServices.Settings?.Current;

        // Видимый тост подавляется при выключенных уведомлениях (ошибки — всегда).
        // Звук управляется отдельно: «звук без уведомлений» — допустимый режим.
        var showVisual = settings is not { ShowNotifications: false } || kind == NotifyKind.Error;

        var now = DateTime.UtcNow;
        var key = $"{kind}|{title}|{message}";
        lock (_gate)
        {
            if (showVisual)
            {
                if (key == _lastKey && now - _lastKeyAt < TimeSpan.FromSeconds(4)) return;
                _lastKey = key; _lastKeyAt = now;   // дедуп-ключ обновляем только когда реально показываем тост
            }
            var sound = settings is null or { PlaySounds: true } ? PickSound(kind, title) : ToastSound.None;
            if (sound != ToastSound.None && now - _lastSoundAt >= TimeSpan.FromMilliseconds(700))
            {
                _lastSoundAt = now;
                ToastSounds.Play(sound);
            }
        }

        if (!showVisual) return;

        if (_ui.CheckAccess()) ShowCore(title, message, kind, clipPath, hint, onClick);
        else _ui.BeginInvoke(() => ShowCore(title, message, kind, clipPath, hint, onClick));
    }

    ToastSound PickSound(NotifyKind kind, string title)
    {
        switch (kind)
        {
            case NotifyKind.Recording:
                return ToastSound.RecordStart;
            case NotifyKind.Success:
                return (DateTime.UtcNow - _lastRecordingStop) < TimeSpan.FromSeconds(5) && title.StartsWith("Запись", StringComparison.OrdinalIgnoreCase)
                    ? ToastSound.RecordStop
                    : ToastSound.Saved;
            case NotifyKind.Error:
                return ToastSound.Error;
            default:
                return ToastSound.None;
        }
    }

    void ShowCore(string title, string message, NotifyKind kind, string? clipPath, string? hint, Action? onClick)
    {
        try
        {
            _host ??= new ToastHostWindow();
            _host.Add(new ToastItem(title, message, kind, clipPath, hint, onClick));
        }
        catch (Exception ex) { Log.Error("Toast failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (AppServices.Engine is { } engine) engine.StateChanged -= OnEngineStateChanged;
        if (_ui.CheckAccess()) CloseHost();
        else _ui.BeginInvoke(CloseHost);
    }

    void CloseHost()
    {
        try { _host?.Close(); } catch { }
        _host = null;
    }
}

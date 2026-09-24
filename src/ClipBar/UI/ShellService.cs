using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClipBar.Core;
using ClipBar.UI.Overlay;
using ClipBar.UI.Pages;

namespace ClipBar.UI;

public sealed class ShellService : IShell
{
    readonly Dispatcher _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    MainWindow? _main;
    OverlayWindow? _overlay;

    public MainWindow Main
    {
        get
        {
            if (_main is null)
            {
                _main = new MainWindow();
                if (Application.Current is { } app) app.MainWindow = _main;
            }
            return _main;
        }
    }

    public void ShowMain(string? page = null) => Run(() =>
    {
        var w = Main;
        w.ShowAndActivate();
        if (page is not null) w.NavigateTo(page);
    });

    public void OpenEditor(string clipPath) => Run(() =>
    {
        if (string.IsNullOrWhiteSpace(clipPath)) return;
        var ext = Path.GetExtension(clipPath).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")
        {
            if (!File.Exists(clipPath))
            {
                AppServices.Notifier?.Show("Файл не найден", Path.GetFileName(clipPath), NotifyKind.Warning);
                return;
            }
            try { _main?.Hide(); (_overlay ??= new OverlayWindow()).ShowImage(clipPath); }
            catch (Exception ex)
            {
                Log.Error("Overlay image viewer failed", ex);
                Process.Start(new ProcessStartInfo(clipPath) { UseShellExecute = true });
            }
            return;
        }

        try
        {
            _main?.Hide();
            (_overlay ??= new OverlayWindow()).ShowEditor(clipPath);
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Overlay editor failed", ex);
            AppServices.Notifier?.Show("Не удалось открыть редактор", ex.Message, NotifyKind.Error);
        }
    });

    public void WarmUpOverlay() => _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
    {
        try { (_overlay ??= new OverlayWindow()).WarmUp(); }
        catch (Exception ex) { Log.Warn("Overlay warm-up failed: " + ex.Message); }
    });

    public void ToggleOverlay() => Run(() =>
    {
        var overlay = _overlay ??= new OverlayWindow();
        if (overlay.IsOverlayVisible) overlay.HideOverlay();
        else overlay.ShowOverlay();
    });

    public void ShowOverlay(bool gallery = false) => Run(() =>
    {
        _main?.Hide();
        var overlay = _overlay ??= new OverlayWindow();
        overlay.ShowOverlay();
        if (gallery) overlay.ShowGalleryView();
    });

    public void HideOverlay() => Run(() => _overlay?.HideOverlay());

    public void StartRegionScreenshot() => Run(() =>
    {
        _overlay?.HideOverlay();
        Screenshot.RegionCaptureWindow.Start();
    });

    public void ExitApp() => Run(() => _ = App.Current.ExitAppAsync());

    internal void CloseWindows()
    {
        Run(() =>
        {
            try { if (_overlay is { } ov) { ov.AllowRealClose = true; ov.Close(); } } catch { }
            try { _main?.Close(); } catch { }
        });
    }

    void Run(Action action)
    {
        if (_ui.CheckAccess()) Safe(action);
        else _ui.BeginInvoke(() => Safe(action));
    }

    static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log.Error("Shell action failed", ex);
            AppServices.Notifier?.Show("Ошибка", ex.Message, NotifyKind.Error);
        }
    }
}

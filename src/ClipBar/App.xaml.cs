using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClipBar.Core;
using ClipBar.UI;
using ClipBar.UI.Notifications;
using ClipBar.UI.Tray;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ClipBar;

public partial class App : Application
{
    const string MutexName = @"Local\ClipBar.SingleInstance.v1";
    const string ShowEventName = @"Local\ClipBar.ShowMain.v1";

    Mutex? _mutex;
    bool _ownsMutex;
    EventWaitHandle? _showEvent;
    RegisteredWaitHandle? _showWait;
    TrayIconController? _tray;
    ShellService? _shell;
    ToastNotifier? _toasts;
    Dictionary<HotkeyAction, string>? _appliedHotkeys;
    bool _exiting, _cleanedUp;

    public static new App Current => (App)Application.Current;

    public bool IsExiting => _exiting;

    partial void OnDevStartup(string[] args);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, false);
            _ = Uninstaller.RunAsync(quiet: e.Args.Contains("--quiet", StringComparer.OrdinalIgnoreCase))
                .ContinueWith(_ => Dispatcher.Invoke(() => Shutdown()));
            return;
        }

        _mutex = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch (Exception ex) { Log.Warn($"Second instance: could not signal the first one: {ex.Message}"); }
            Shutdown();
            return;
        }

        HookGlobalHandlers();
        Log.Info($"ClipBar {typeof(App).Assembly.GetName().Version} starting (args: {string.Join(' ', e.Args)})");

        try
        {
            AppPaths.EnsureCreated();
            var firstRun = !File.Exists(AppPaths.SettingsFile);

            var settings = new SettingsService();
            settings.Load();
            AppServices.Settings = settings;
            SyncAutostart(settings, firstRun);

            if (UpdateService.ApplyPendingAtStartup()) { Shutdown(); return; }

            ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, false);

            AppServices.Audio = new Audio.AudioHub(settings);
            AppServices.Engine = new Capture.CaptureEngine(settings, AppServices.Audio);
            AppServices.Library = new Library.ClipLibrary(settings);
            _toasts = new ToastNotifier();
            AppServices.Notifier = _toasts;
            _shell = new ShellService();
            AppServices.Shell = _shell;

            var hotkeys = new Hotkeys.HotkeyService();
            AppServices.Hotkeys = hotkeys;
            hotkeys.Initialize();
            hotkeys.Pressed += (_, action) => _ = Actions.RunAsync(action);
            ApplyHotkeys(settings.Current);
            settings.Changed += (_, s) => Dispatcher.BeginInvoke(() => ApplyHotkeys(s));

            _tray = new TrayIconController();

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
                (_, _) => Dispatcher.BeginInvoke(() => _shell?.ShowOverlay()), null, -1, false);

            OnDevStartup(e.Args);

            var minimized = settings.Current.StartMinimized;
            if (firstRun)
            {
                settings.Save();
                _shell.ShowMain();
                AppServices.Notifier.Show("ClipBar работает в фоне",
                    $"{Actions.HotkeyText(HotkeyAction.SaveReplay)} — сохранить откат", NotifyKind.Info);
            }
            else if (!minimized)
            {
                _shell.ShowMain();
            }

            if (settings.Current.ReplayEnabled) _ = StartReplayAsync();
            _ = RefreshLibraryAsync();
            _shell.WarmUpOverlay();
            UpdateService.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            System.Windows.MessageBox.Show($"ClipBar не удалось запустить:\n{ex.Message}\n\nЛог: {AppPaths.LogsDir}",
                "ClipBar", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    static void SyncAutostart(SettingsService settings, bool firstRun)
    {
        try
        {
            var current = UI.Pages.StartupRegistry.CurrentValue();
            if (firstRun && settings.Current.LaunchAtStartup && current is null)
            {
                UI.Pages.StartupRegistry.Set(true);
                return;
            }
            settings.Current.LaunchAtStartup = current is not null;
            if (current is not null && current != UI.Pages.StartupRegistry.Command)
            {
                UI.Pages.StartupRegistry.Set(true);
                Log.Info($"Autostart path updated: {current} -> {UI.Pages.StartupRegistry.Command}");
            }
        }
        catch (Exception ex) { Log.Warn("Autostart sync failed: " + ex.Message); }
    }

    static async Task StartReplayAsync()
    {
        try { await AppServices.Engine.StartReplayAsync(); }
        catch (Exception ex)
        {
            Log.Error("Replay auto-start failed", ex);
            AppServices.Notifier.Show("Не удалось запустить откат", ex.Message, NotifyKind.Error);
        }
    }

    static async Task RefreshLibraryAsync()
    {
        try { await AppServices.Library.RefreshAsync(); }
        catch (Exception ex) { Log.Error("Library refresh failed", ex); }
    }

    void ApplyHotkeys(AppSettings settings)
    {
        try
        {
            var wanted = settings.Hotkeys;
            if (_appliedHotkeys is not null && _appliedHotkeys.Count == wanted.Count
                && wanted.All(kv => _appliedHotkeys.TryGetValue(kv.Key, out var v) && v == kv.Value))
                return;

            _appliedHotkeys = new Dictionary<HotkeyAction, string>(wanted);
            var bindings = wanted.ToDictionary(kv => kv.Key, kv => HotkeyBinding.Parse(kv.Value));
            var failed = AppServices.Hotkeys.Apply(bindings);
            if (failed.Count > 0)
            {
                var list = string.Join(", ", failed.Select(a => $"{a.Title()} ({Actions.HotkeyText(a)})"));
                Log.Warn($"Hotkeys not registered: {list}");
                AppServices.Notifier.Show("Горячие клавиши заняты другой программой", list, NotifyKind.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error("ApplyHotkeys failed", ex);
            AppServices.Notifier?.Show("Ошибка горячих клавиш", ex.Message, NotifyKind.Error);
        }
    }

    void HookGlobalHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            Log.Error("Unhandled UI exception", args.Exception);
            TryToast("Что-то пошло не так", args.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception (AppDomain)", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            Log.Error("Unobserved task exception", args.Exception);
            TryToast("Ошибка в фоне", args.Exception.InnerException?.Message ?? args.Exception.Message);
        };
    }

    static void TryToast(string title, string message)
    {
        try { AppServices.Notifier?.Show(title, message, NotifyKind.Error); } catch { }
    }

    public async Task ExitAppAsync()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Exit requested");
        try
        {
            var engine = AppServices.Engine;
            if (engine is { IsRecording: true })
            {
                try
                {
                    var path = await engine.StopRecordingAsync();
                    await AppServices.Library.AddAsync(path);
                    Log.Info($"Recording saved on exit: {path}");
                }
                catch (Exception ex) { Log.Error("Stopping the recording on exit failed", ex); }
            }
            if (engine is not null)
            {
                try { await engine.StopReplayAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception ex) { Log.Error("Stopping the replay buffer on exit failed", ex); }
            }
        }
        finally
        {
            CleanupServices();
            Shutdown();
        }
    }

    void CleanupServices()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        _exiting = true;
        Try(() => _tray?.Dispose(), "tray");
        Try(() => AppServices.Hotkeys?.Dispose(), "hotkeys");
        Try(() => AppServices.Engine?.Dispose(), "engine");
        Try(() => AppServices.Audio?.Dispose(), "audio");
        Try(() => _toasts?.Dispose(), "toasts");
        Try(() => _shell?.CloseWindows(), "windows");
        Log.Info("Services disposed");

        static void Try(Action a, string what)
        {
            try { a(); } catch (Exception ex) { Log.Error($"Dispose {what} failed", ex); }
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Log.Info($"Session ending: {e.ReasonSessionEnding}");
        CleanupServices();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) CleanupServices();
        try { _showWait?.Unregister(null); } catch { }
        _showEvent?.Dispose();
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

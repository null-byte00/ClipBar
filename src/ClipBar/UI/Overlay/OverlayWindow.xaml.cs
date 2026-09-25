using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClipBar.Core;
using ClipBar.UI.Overlay.Widgets;
using ClipBar.UI.Pages;
using Wpf.Ui.Controls;

namespace ClipBar.UI.Overlay;

public partial class OverlayWindow : Window, IOverlayHost
{
    enum OverlayState { Hidden, Visible, Hiding }

    public static bool ExcludeFromCapture { get; set; } =
        Environment.GetEnvironmentVariable("CLIPBAR_OVERLAY_CAPTURABLE") != "1";

    public bool AllowRealClose { get; set; }

    const double PanelSlideFrom = -40;

    OverlayState _state;
    IntPtr _hwnd;
    IntPtr _prevForeground;
    OverlayNative.RECT _bounds;
    bool _excluded;
    bool _monitoring;

    AudioWidget? _audio;
    GalleryWidget? _gallery;
    GalleryWidget? _shots;
    ScreenshotViewer? _viewer;
    PerformanceWidget? _perf;
    readonly List<IOverlayWidget> _active = new();

    GalleryPage? _galleryPage;
    EditorPage? _editorPage;
    string? _contentView;

    readonly DispatcherTimer _fastTimer;
    readonly DispatcherTimer _slowTimer;

    public OverlayWindow()
    {
        InitializeComponent();
        _fastTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        _fastTimer.Tick += (_, _) => TickWidgets(fast: true);
        _slowTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _slowTimer.Tick += (_, _) => { TickWidgets(fast: false); RefreshPanel(); };
    }

    public bool IsOverlayVisible => _state == OverlayState.Visible;

    public void WarmUp()
    {
        new WindowInteropHelper(this).EnsureHandle();
        EnsureWidgets();
        Root.Measure(new Size(1920, 1080));
        Root.Arrange(new Rect(0, 0, 1920, 1080));
    }
    bool IOverlayHost.ExcludedFromCapture => _excluded;

    public void ShowOverlay()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ShowOverlay); return; }
        try
        {
            if (_state == OverlayState.Visible) return;
            var wasHiding = _state == OverlayState.Hiding;
            Root.BeginAnimation(OpacityProperty, null);
            PanelSlide.BeginAnimation(TranslateTransform.XProperty, null);

            if (!wasHiding)
            {
                var fg = OverlayNative.GetForegroundWindow();
                _prevForeground = fg == _hwnd ? IntPtr.Zero : fg;
            }

            (_bounds, _) = OverlayNative.GetMonitorUnderCursor();
            new WindowInteropHelper(this).EnsureHandle();

            // Впереди полноэкранная/безрамочная игра? Тогда не активируем окно и не воруем
            // передний план — иначе игра (Minecraft F11 и т.п.) сворачивается. Оверлей всё равно
            // топ-мост и ляжет поверх; закрыть можно тем же Alt+Z или кликом.
            var overGame = _prevForeground != IntPtr.Zero && OverlayNative.IsFullscreen(_prevForeground);
            OverlayNative.SetNoActivate(_hwnd, overGame);

            ApplyBounds();

            EnsureWidgets();
            Root.Opacity = 0;
            PanelSlide.X = PanelSlideFrom;
            _state = OverlayState.Visible;
            Show();
            ApplyBounds();

            if (!overGame)
            {
                Activate();
                OverlayNative.ForceForeground(_hwnd);
                Root.Focus();
                Keyboard.Focus(Root);
            }
            else
            {
                OverlayNative.SetWindowPos(_hwnd, OverlayNative.HWND_TOPMOST, _bounds.Left, _bounds.Top,
                    _bounds.Width, _bounds.Height, OverlayNative.SWP_NOACTIVATE | OverlayNative.SWP_SHOWWINDOW);
                Root.Focus();
            }

            if (AppServices.Engine is { } engine) engine.StateChanged += OnEngineStateChanged;
            ActivateWidget(_audio!);
            ActivateWidget(_gallery!);
            ActivateWidget(_shots!);
            if (PerfExpander.IsExpanded) ActivateWidget(_perf!);
            RefreshPanel();
            UpdateTimers();
            SetMonitoring(true);

            HintText.Text = $"Esc или {Actions.HotkeyText(HotkeyAction.ToggleOverlay)} — закрыть  ·  клик по игре — тоже";
            AnimateIn();
        }
        catch (Exception ex)
        {
            Log.Error("ShowOverlay failed", ex);
            _state = OverlayState.Hidden;
            if (AppServices.Engine is { } engine) engine.StateChanged -= OnEngineStateChanged;
            DeactivateAll();
            try { Hide(); } catch { }
        }
    }

    public void HideOverlay()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(HideOverlay); return; }
        try
        {
            if (_state != OverlayState.Visible) return;
            _state = OverlayState.Hiding;
            if (AppServices.Engine is { } engine) engine.StateChanged -= OnEngineStateChanged;
            DeactivateAll();
            UpdateTimers();
            SetMonitoring(false);

            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (_, _) => { if (_state == OverlayState.Hiding) CompleteHide(); };
            Root.BeginAnimation(OpacityProperty, fade);
        }
        catch (Exception ex)
        {
            Log.Error("HideOverlay failed", ex);
            CompleteHide();
        }
    }

    void CompleteHide()
    {
        try
        {
            CloseContent();
            Hide();
            Root.BeginAnimation(OpacityProperty, null);
            Root.Opacity = 0;
            _state = OverlayState.Hidden;
            RestoreForeground();
        }
        catch (Exception ex) { Log.Error("Overlay hide completion failed", ex); _state = OverlayState.Hidden; }
    }

    void AnimateIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };
        fade.Completed += (_, _) => { if (_state == OverlayState.Visible) { Root.BeginAnimation(OpacityProperty, null); Root.Opacity = 1; } };
        Root.BeginAnimation(OpacityProperty, fade);
        var slide = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
        slide.Completed += (_, _) => { PanelSlide.BeginAnimation(TranslateTransform.XProperty, null); PanelSlide.X = 0; };
        PanelSlide.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    public void ShowEditor(string clipPath)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowEditor(clipPath)); return; }
        try
        {
            if (_state != OverlayState.Visible) ShowOverlay();
            if (_editorPage is null)
            {
                _editorPage = new EditorPage();
                _editorPage.ClipDeleted += ShowGallery;
            }
            ShowContent("editor", _editorPage, System.IO.Path.GetFileNameWithoutExtension(clipPath));
            _editorPage.LoadClip(clipPath);
        }
        catch (Exception ex)
        {
            Log.Error("Overlay ShowEditor failed", ex);
            AppServices.Notifier?.Show("Не удалось открыть редактор", ex.Message, NotifyKind.Error);
        }
    }

    public void ShowGalleryView() => ShowGallery();

    public void ShowImage(string path)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowImage(path)); return; }
        try
        {
            if (_state != OverlayState.Visible) ShowOverlay();
            if (_viewer is null)
            {
                _viewer = new ScreenshotViewer();
                _viewer.Emptied += ShowGallery;
                _viewer.Shown += p => { if (_contentView == "image") ContentTitle.Text = System.IO.Path.GetFileNameWithoutExtension(p); };
            }
            ShowContent("image", _viewer, System.IO.Path.GetFileNameWithoutExtension(path));
            _viewer.Show(path);
        }
        catch (Exception ex)
        {
            Log.Error("Overlay ShowImage failed", ex);
            AppServices.Notifier?.Show("Не удалось открыть скриншот", ex.Message, NotifyKind.Error);
        }
    }

    void ShowGallery()
    {
        try
        {
            _galleryPage ??= new GalleryPage();
            ShowContent("gallery", _galleryPage, "Галерея");
        }
        catch (Exception ex) { Log.Error("Overlay ShowGallery failed", ex); }
    }

    void ShowContent(string view, object page, string title)
    {
        _contentView = view;
        ContentTitle.Text = title;
        BackButton.Visibility = view is "editor" or "image" ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(ContentFrame.Content, page)) ContentFrame.Content = page;
        if (ContentCard.Visibility != Visibility.Visible)
        {
            ContentCard.Visibility = Visibility.Visible;
            ContentCard.Opacity = 0;
            ContentCard.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)) { FillBehavior = FillBehavior.Stop });
            ContentCard.Opacity = 1;
        }
    }

    void CloseContent()
    {
        _contentView = null;
        ContentFrame.Content = null;
        ContentCard.Visibility = Visibility.Collapsed;
    }

    void Back_Click(object sender, RoutedEventArgs e) => ShowGallery();
    void CloseContent_Click(object sender, RoutedEventArgs e) => CloseContent();

    void ReplayTile_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.Engine?.ReplayActive == true) _ = Actions.SaveReplayAsync();
        else _ = RunThenRefresh(Actions.ToggleReplayBufferAsync());
    }

    void RecordTile_Click(object sender, RoutedEventArgs e)
    {
        var starting = AppServices.Engine?.IsRecording != true;
        _ = RunThenRefresh(Actions.ToggleRecordingAsync());
        if (starting) HideOverlay();
    }

    void ScreenshotTile_Click(object sender, RoutedEventArgs e)
    {
        if (_excluded) _ = Actions.ScreenshotAsync();
        else { HideOverlay(); _ = DelayedScreenshot(); }
    }

    static async Task DelayedScreenshot()
    {
        await Task.Delay(250);
        await Actions.ScreenshotAsync();
    }

    void GalleryTile_Click(object sender, RoutedEventArgs e) => ShowGallery();

    void ReplaySwitch_Click(object sender, RoutedEventArgs e) => _ = RunThenRefresh(Actions.ToggleReplayBufferAsync());

    void SaveChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out var seconds)) return;
        _ = Actions.SaveReplayAsync(seconds > 0 ? TimeSpan.FromSeconds(seconds) : null);
    }

    async Task RunThenRefresh(Task t)
    {
        try { await t; } catch (Exception ex) { Log.Error("Overlay action failed", ex); }
        RefreshPanel();
    }

    void PerfExpander_Changed(object sender, RoutedEventArgs e)
    {
        if (_perf is null || _state != OverlayState.Visible) return;
        if (PerfExpander.IsExpanded) ActivateWidget(_perf); else DeactivateWidget(_perf);
        UpdateTimers();
    }

    void RefreshPanel()
    {
        if (_state != OverlayState.Visible) return;
        try
        {
            var engine = AppServices.Engine;
            var settings = AppServices.Settings?.Current;
            var replayOn = engine?.ReplayActive == true;
            var total = TimeSpan.FromSeconds(settings?.ReplaySeconds ?? 300);
            var buffered = engine?.BufferedDuration ?? TimeSpan.Zero;
            if (buffered > total) buffered = total;

            ClockText.Text = $"by @lumaseller  ·  {DateTime.Now:HH:mm}";

            ReplayTileStatus.Text = replayOn
                ? $"Сохранить · {Actions.HotkeyText(HotkeyAction.SaveReplay)}"
                : "Выкл — нажми, чтобы включить";
            ReplayStateText.Text = !replayOn ? "Выключен"
                : engine!.State == EngineState.Error ? "Ошибка захвата"
                : engine.State == EngineState.Starting ? "Запускается…" : "Включён";
            ReplayDetailText.Text = !replayOn
                ? $"Включи, чтобы всегда хранить последние {Actions.FormatDuration(total)}"
                : engine!.State == EngineState.Error ? engine.LastError ?? ""
                : $"В буфере {Clock(buffered)} из {Actions.FormatDuration(total)}" +
                  (engine.ActiveEncoder is { } enc ? $" · {enc}" : "");
            if (ReplaySwitch.IsChecked != replayOn) ReplaySwitch.IsChecked = replayOn;
            var track = ReplayFill.Parent is FrameworkElement p ? p.ActualWidth : 0;
            ReplayFill.Width = replayOn && total.TotalSeconds > 0 ? track * buffered.TotalSeconds / total.TotalSeconds : 0;
            ReplayChips.IsEnabled = replayOn && buffered > TimeSpan.Zero;
            FullChip.Content = $"Всё ({Actions.FormatDuration(total)})".Replace(" сек)", "с)").Replace(" мин)", "м)");

            var rec = engine?.IsRecording == true;
            RecordTileIcon.Symbol = rec ? SymbolRegular.RecordStop24 : SymbolRegular.Record24;
            RecordTileTitle.Text = rec ? "Остановить" : "Запись";
            RecordTileStatus.Text = rec ? Clock(engine!.RecordingElapsed) : Actions.HotkeyText(HotkeyAction.ToggleRecording);
            RecIndicator.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
            if (rec) RecIndicatorText.Text = Clock(engine!.RecordingElapsed);

            ScreenshotTileStatus.Text = Actions.HotkeyText(HotkeyAction.Screenshot);
        }
        catch (Exception ex) { Log.Error("Overlay panel refresh failed", ex); }
    }

    static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    void OnEngineStateChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshPanel);

    void EnsureWidgets()
    {
        if (_audio is not null) return;
        _audio = new AudioWidget { Host = this, Width = double.NaN };
        _gallery = new GalleryWidget { Host = this, Width = double.NaN, Screenshots = false };
        _shots = new GalleryWidget { Host = this, Width = double.NaN, Screenshots = true };
        ShotsHost.Content = _shots;
        _perf = new PerformanceWidget { Host = this, Width = double.NaN };
        AudioHost.Content = _audio;
        GalleryHost.Content = _gallery;
        PerfHost.Content = _perf;
    }

    void ActivateWidget(IOverlayWidget w)
    {
        if (_active.Contains(w)) return;
        _active.Add(w);
        try { w.Activate(); } catch (Exception ex) { Log.Error($"Widget '{w.Id}' activate failed", ex); }
    }

    void DeactivateWidget(IOverlayWidget w)
    {
        if (!_active.Remove(w)) return;
        try { w.Deactivate(); } catch (Exception ex) { Log.Error($"Widget '{w.Id}' deactivate failed", ex); }
    }

    void DeactivateAll()
    {
        foreach (var w in _active.ToArray()) DeactivateWidget(w);
    }

    void TickWidgets(bool fast)
    {
        foreach (var w in _active)
        {
            try
            {
                if (fast) { if (w.NeedsFastTick) w.FastTick(); }
                else w.SlowTick();
            }
            catch (Exception ex) { Log.Error($"Widget '{w.Id}' tick failed", ex); }
        }
    }

    void UpdateTimers()
    {
        var visible = _state == OverlayState.Visible;
        var needFast = visible && _active.Any(w => w.NeedsFastTick);
        if (needFast != _fastTimer.IsEnabled) { if (needFast) _fastTimer.Start(); else _fastTimer.Stop(); }
        if (visible != _slowTimer.IsEnabled) { if (visible) _slowTimer.Start(); else _slowTimer.Stop(); }
    }

    void SetMonitoring(bool want)
    {
        if (want == _monitoring) return;
        _monitoring = want;
        try { AppServices.Audio?.SetMonitoring(want); }
        catch (Exception ex) { Log.Error("SetMonitoring failed", ex); }
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (IsAnyDropDownOpen(this)) return;
        if (_contentView is "editor" or "image") ShowGallery();
        else if (_contentView is not null) CloseContent();
        else HideOverlay();
        e.Handled = true;
    }

    static bool IsAnyDropDownOpen(DependencyObject root)
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ComboBox { IsDropDownOpen: true }) return true;
            if (IsAnyDropDownOpen(child)) return true;
        }
        return false;
    }

    void Dim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_contentView is not null) CloseContent();
        else HideOverlay();
        e.Handled = true;
    }

    void Close_Click(object sender, RoutedEventArgs e) => HideOverlay();

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        try { AppServices.Shell?.ShowMain("settings"); }
        catch (Exception ex) { Log.Error("ShowMain(settings) failed", ex); }
        HideOverlay();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        OverlayNative.MakeToolWindow(_hwnd);
        EnableGpuTransparency(_hwnd);
        if (ExcludeFromCapture)
        {
            _excluded = OverlayNative.SetWindowDisplayAffinity(_hwnd, OverlayNative.WDA_EXCLUDEFROMCAPTURE);
            if (!_excluded) Log.Warn("SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) failed — overlay will be visible in captures");
        }
    }

    void EnableGpuTransparency(IntPtr hwnd)
    {
        try
        {
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            var hr = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            if (hr != 0) Log.Warn($"DwmExtendFrameIntoClientArea failed: 0x{hr:X8}");
        }
        catch (Exception ex) { Log.Warn("GPU transparency unavailable: " + ex.Message); }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowRealClose && Application.Current is { } app && !app.Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            HideOverlay();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        DeactivateAll();
        _fastTimer.Stop();
        _slowTimer.Stop();
        CloseContent();
        base.OnClosed(e);
    }

    void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (OverlayNative.GetWindowRect(_hwnd, out var r) &&
            r.Left == _bounds.Left && r.Top == _bounds.Top && r.Width == _bounds.Width && r.Height == _bounds.Height) return;
        OverlayNative.SetWindowPos(_hwnd, OverlayNative.HWND_TOPMOST, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height,
            OverlayNative.SWP_NOACTIVATE);
    }

    void RestoreForeground()
    {
        var prev = _prevForeground;
        _prevForeground = IntPtr.Zero;
        if (prev == IntPtr.Zero || prev == _hwnd || !OverlayNative.IsWindow(prev)) return;
        OverlayNative.SetForegroundWindow(prev);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
}

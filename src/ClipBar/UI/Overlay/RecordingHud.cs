using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClipBar.Capture;
using ClipBar.Core;

namespace ClipBar.UI.Overlay;

/// <summary>
/// Маленький плавающий индикатор «● REC 00:42 · 60 fps» — висит поверх игры
/// во время записи, полный оверлей открывать не нужно. Из захвата исключается
/// вместе с оверлеем (та же настройка OverlayVisibleInCapture).
/// </summary>
internal sealed class RecordingHud : Window
{
    readonly ICaptureEngine _engine;
    readonly DispatcherTimer _timer;
    readonly TextBlock _timeText;
    readonly TextBlock _fpsText;
    IntPtr _hwnd;

    public RecordingHud(ICaptureEngine engine)
    {
        _engine = engine;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)), VerticalAlignment = VerticalAlignment.Center };
        dot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(800))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });

        _timeText = new TextBlock
        {
            Text = "00:00",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fpsText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x14, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { dot, _timeText, _fpsText },
            },
        };

        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => UpdateText();

        _engine.StateChanged += OnStateChanged;
        if (_engine.IsRecording) ShowHud();
    }

    void OnStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_engine.IsRecording) ShowHud();
            else HideHud();
        });
    }

    void ShowHud()
    {
        if (!IsVisible)
        {
            PositionOnMonitor();
            Show();
            ApplyAffinity();
            _timer.Start();
        }
        UpdateText();
    }

    void HideHud()
    {
        _timer.Stop();
        if (IsVisible) Hide();
    }

    void UpdateText()
    {
        var t = _engine.RecordingElapsed;
        _timeText.Text = t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";

        var s = AppServices.Settings?.Current;
        if (s is not null)
        {
            var hz = ScreenGrabber.GetRefreshHz(s.MonitorIndex);
            var fps = hz > 0 ? Math.Min(s.Fps, hz) : s.Fps;
            _fpsText.Text = $"· {fps} fps";
        }
    }

    void PositionOnMonitor()
    {
        var s = AppServices.Settings?.Current;
        var m = ScreenGrabber.GetMonitor(s?.MonitorIndex ?? 0);
        // правый верхний угол захватываемого монитора, отступ 16px
        Left = m.X + m.Width - 170;
        Top = m.Y + 16;
    }

    void ApplyAffinity()
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;
        OverlayNative.MakeToolWindow(_hwnd);
        var visible = AppServices.Settings?.Current.OverlayVisibleInCapture == true;
        OverlayNative.SetWindowDisplayAffinity(_hwnd,
            visible ? OverlayNative.WDA_NONE : OverlayNative.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
    }
}

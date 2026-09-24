using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClipBar.Core;
using Wpf.Ui.Controls;

namespace ClipBar.UI.Notifications;

public partial class ToastItem : UserControl
{
    static readonly TimeSpan AutoHide = TimeSpan.FromSeconds(3.5);
    readonly DispatcherTimer _timer = new() { Interval = AutoHide };
    readonly string? _clipPath;
    bool _closing;

    public event EventHandler? Dismissed;

    readonly Action? _onClick;

    public ToastItem(string title, string message, NotifyKind kind, string? clipPath, string? hint = null, Action? onClick = null)
    {
        InitializeComponent();
        _clipPath = clipPath;
        _onClick = onClick;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        ApplyKind(kind);

        if (!string.IsNullOrEmpty(clipPath) || onClick is not null)
        {
            if (hint is not null) HintText.Text = hint;
            HintText.Visibility = Visibility.Visible;
            Panel.Cursor = Cursors.Hand;
        }

        _timer.Tick += (_, _) => Dismiss();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => { if (!_closing) _timer.Start(); };
        Loaded += (_, _) => { AnimateIn(); _timer.Start(); };
        Opacity = 0;
    }

    void ApplyKind(NotifyKind kind)
    {
        Brush Res(string key) => (Brush)FindResource(key);
        switch (kind)
        {
            case NotifyKind.Success:
                KindIcon.Symbol = SymbolRegular.CheckmarkCircle24;
                KindIcon.Foreground = Res("CB.Success");
                break;
            case NotifyKind.Warning:
                KindIcon.Symbol = SymbolRegular.Warning24;
                KindIcon.Foreground = Res("CB.Warning");
                break;
            case NotifyKind.Error:
                KindIcon.Symbol = SymbolRegular.ErrorCircle24;
                KindIcon.Foreground = Res("CB.Error");
                break;
            case NotifyKind.Recording:
                KindIcon.Visibility = Visibility.Collapsed;
                RecordDot.Visibility = Visibility.Visible;
                StartPulse();
                break;
            default:
                KindIcon.Symbol = SymbolRegular.Info24;
                KindIcon.Foreground = Res("CB.Accent");
                break;
        }
    }

    void StartPulse()
    {
        var scale = new DoubleAnimation(0.6, 1.15, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        var fade = new DoubleAnimation(0.45, 0.12, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        RecordPulse.BeginAnimation(OpacityProperty, fade);
    }

    void AnimateIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(56, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        IsHitTestVisible = false;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        fade.Completed += (_, _) => Dismissed?.Invoke(this, EventArgs.Empty);
        Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, fade);
    }

    void OnCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }

    void OnBodyClick(object sender, MouseButtonEventArgs e)
    {
        if (_onClick is not null)
        {
            try { _onClick(); } catch (Exception ex) { Log.Error("Toast action failed", ex); }
            Dismiss();
            return;
        }
        if (_clipPath is null) return;
        try { AppServices.Shell?.OpenEditor(_clipPath); }
        catch (Exception ex) { Log.Error("Toast open failed", ex); }
        Dismiss();
    }
}

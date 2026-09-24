using System.Windows;
using System.Windows.Media;

namespace ClipBar.UI.Overlay.Controls;

public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(nameof(Level), typeof(double), typeof(LevelMeter),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceLevel));

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(LevelMeter),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(LevelMeter),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HotBrushProperty =
        DependencyProperty.Register(nameof(HotBrush), typeof(Brush), typeof(LevelMeter),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xFC, 0xE1, 0x00)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsMutedProperty =
        DependencyProperty.Register(nameof(IsMuted), typeof(bool), typeof(LevelMeter),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    static object CoerceLevel(DependencyObject d, object v) => Math.Clamp((double)v, 0, 1);

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush HotBrush { get => (Brush)GetValue(HotBrushProperty); set => SetValue(HotBrushProperty, value); }
    public bool IsMuted { get => (bool)GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }

    double _peak;
    DateTime _peakAt;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, 4);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = h / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, w, h), r, r);
        if (IsMuted) return;

        var shown = Math.Pow(Level, 0.6);
        var now = DateTime.UtcNow;
        if (shown >= _peak) { _peak = shown; _peakAt = now; }
        else
        {
            var age = (now - _peakAt).TotalSeconds;
            if (age > 0.6) _peak = Math.Max(shown, _peak - (age - 0.6) * 0.9 * 0.033);
        }

        if (shown > 0.002)
        {
            var fw = Math.Max(h, w * shown);
            dc.DrawRoundedRectangle(shown > 0.92 ? HotBrush : Fill, null, new Rect(0, 0, fw, h), r, r);
        }
        if (_peak > 0.02)
        {
            var px = Math.Clamp(w * _peak - 1, 0, w - 2);
            dc.DrawRectangle(Fill, null, new Rect(px, 0, 2, h));
        }
    }
}

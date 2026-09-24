using System.Windows;
using System.Windows.Media;

namespace ClipBar.UI.Overlay.Controls;

public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
            new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty =
        DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(Sparkline),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(Sparkline),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty =
        DependencyProperty.Register(nameof(Capacity), typeof(int), typeof(Sparkline),
            new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    readonly List<double> _values = new(64);

    public void SetValues(IEnumerable<double> values)
    {
        _values.Clear();
        _values.AddRange(values);
        Trim();
        InvalidateVisual();
    }

    public void Push(double value)
    {
        _values.Add(double.IsFinite(value) ? value : 0);
        Trim();
        InvalidateVisual();
    }

    public void Clear() { _values.Clear(); InvalidateVisual(); }

    void Trim()
    {
        var cap = Math.Max(2, Capacity);
        if (_values.Count > cap) _values.RemoveRange(0, _values.Count - cap);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        var gridPen = new Pen(GridBrush, 1); gridPen.Freeze();
        for (var i = 0; i <= 4; i++)
        {
            var y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }
        var cap = Math.Max(2, Capacity);
        for (var s = 10; s < cap; s += 10)
        {
            var x = Math.Round(w * (cap - s) / (cap - 1)) + 0.5;
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
        }

        if (_values.Count < 2) return;

        var max = Maximum <= 0 ? 100 : Maximum;
        var step = w / (cap - 1);
        var startX = w - step * (_values.Count - 1);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            var first = true;
            Point p0 = default;
            for (var i = 0; i < _values.Count; i++)
            {
                var v = Math.Clamp(_values[i] / max, 0, 1);
                var p = new Point(startX + step * i, h - 1 - v * (h - 2));
                if (first)
                {
                    lc.BeginFigure(p, false, false);
                    ac.BeginFigure(new Point(p.X, h), true, true);
                    ac.LineTo(p, false, false);
                    p0 = p; first = false;
                }
                else
                {
                    lc.LineTo(p, true, true);
                    ac.LineTo(p, false, false);
                }
            }
            ac.LineTo(new Point(startX + step * (_values.Count - 1), h), false, false);
            _ = p0;
        }
        line.Freeze(); area.Freeze();

        var stroke = Stroke;
        Brush fill;
        if (stroke is SolidColorBrush scb)
        {
            var c = scb.Color;
            fill = new LinearGradientBrush(
                Color.FromArgb(0x60, c.R, c.G, c.B), Color.FromArgb(0x08, c.R, c.G, c.B), 90);
        }
        else fill = new SolidColorBrush(Color.FromArgb(0x40, 0x4C, 0xC2, 0xFF));
        fill.Freeze();

        dc.DrawGeometry(fill, null, area);
        var pen = new Pen(stroke, 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}

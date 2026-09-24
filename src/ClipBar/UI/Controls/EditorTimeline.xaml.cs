using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClipBar.UI.Controls;

public partial class EditorTimeline : UserControl
{
    const double HandleWidth = 14;
    static readonly TimeSpan MinRange = TimeSpan.FromMilliseconds(100);

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(TimeSpan), typeof(EditorTimeline), new FrameworkPropertyMetadata(TimeSpan.Zero, OnGeometryChanged));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(TimeSpan), typeof(EditorTimeline), new FrameworkPropertyMetadata(TimeSpan.Zero, OnPositionChanged));
    public static readonly DependencyProperty InPointProperty = DependencyProperty.Register(
        nameof(InPoint), typeof(TimeSpan), typeof(EditorTimeline), new FrameworkPropertyMetadata(TimeSpan.Zero, OnRangeChangedDp));
    public static readonly DependencyProperty OutPointProperty = DependencyProperty.Register(
        nameof(OutPoint), typeof(TimeSpan), typeof(EditorTimeline), new FrameworkPropertyMetadata(TimeSpan.Zero, OnRangeChangedDp));
    public static readonly DependencyProperty FilmstripProperty = DependencyProperty.Register(
        nameof(Filmstrip), typeof(ImageSource), typeof(EditorTimeline), new PropertyMetadata(null, OnImagesChanged));
    public static readonly DependencyProperty WaveformProperty = DependencyProperty.Register(
        nameof(Waveform), typeof(ImageSource), typeof(EditorTimeline), new PropertyMetadata(null, OnImagesChanged));
    public static readonly DependencyProperty HasAudioProperty = DependencyProperty.Register(
        nameof(HasAudio), typeof(bool), typeof(EditorTimeline), new PropertyMetadata(true, OnImagesChanged));

    public TimeSpan Duration { get => (TimeSpan)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public TimeSpan Position { get => (TimeSpan)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public TimeSpan InPoint { get => (TimeSpan)GetValue(InPointProperty); set => SetValue(InPointProperty, value); }
    public TimeSpan OutPoint { get => (TimeSpan)GetValue(OutPointProperty); set => SetValue(OutPointProperty, value); }
    public ImageSource? Filmstrip { get => (ImageSource?)GetValue(FilmstripProperty); set => SetValue(FilmstripProperty, value); }
    public ImageSource? Waveform { get => (ImageSource?)GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    public bool HasAudio { get => (bool)GetValue(HasAudioProperty); set => SetValue(HasAudioProperty, value); }

    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler? RangeChanged;
    public event EventHandler<bool>? ScrubbingChanged;

    public double TrackWidth => TrackArea.ActualWidth;

    enum DragMode { None, Seek, In, Out }
    DragMode _drag;
    bool _rangeDirty;

    public EditorTimeline()
    {
        InitializeComponent();
        TrackArea.SizeChanged += (_, _) => Relayout();
        RulerCanvas.SizeChanged += (_, _) => DrawRuler();
        Root.MouseLeftButtonDown += OnMouseDown;
        Root.MouseMove += OnMouseMove;
        Root.MouseLeftButtonUp += OnMouseUp;
        Root.MouseLeave += (_, _) => SetHover(null);
        Root.LostMouseCapture += (_, _) => EndDrag();
        Loaded += (_, _) => { Relayout(); DrawRuler(); UpdateImages(); };
    }

    double W => TrackArea.ActualWidth;

    double XOf(TimeSpan t) => Duration <= TimeSpan.Zero ? 0 : Math.Clamp(t.TotalSeconds / Duration.TotalSeconds, 0, 1) * W;

    TimeSpan TimeAt(double x)
    {
        if (Duration <= TimeSpan.Zero || W <= 0) return TimeSpan.Zero;
        var f = Math.Clamp(x / W, 0, 1);
        return TimeSpan.FromSeconds(f * Duration.TotalSeconds);
    }

    static void OnGeometryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tl = (EditorTimeline)d;
        tl.DrawRuler();
        tl.Relayout();
    }

    static void OnRangeChangedDp(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((EditorTimeline)d).Relayout();
    static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((EditorTimeline)d).PlacePlayhead();
    static void OnImagesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((EditorTimeline)d).UpdateImages();

    void UpdateImages()
    {
        FilmstripImage.Source = Filmstrip;
        FilmstripHint.Visibility = Filmstrip is null && Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        WaveformImage.Source = Waveform;
        WaveformHint.Visibility = !HasAudio && Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
    }

    void Relayout()
    {
        var w = W;
        var h = TrackArea.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var xIn = XOf(InPoint);
        var xOut = Duration > TimeSpan.Zero ? XOf(OutPoint) : w;
        if (Duration <= TimeSpan.Zero) { xIn = 0; xOut = w; }

        DimLeft.Width = Math.Max(0, xIn);
        DimLeft.Height = h;
        Canvas.SetLeft(DimLeft, 0);
        DimRight.Width = Math.Max(0, w - xOut);
        DimRight.Height = h;
        Canvas.SetLeft(DimRight, xOut);

        var rangeW = Math.Max(0, xOut - xIn);
        RangeTop.Width = rangeW;
        Canvas.SetLeft(RangeTop, xIn);
        Canvas.SetTop(RangeTop, 0);
        RangeBottom.Width = rangeW;
        Canvas.SetLeft(RangeBottom, xIn);
        Canvas.SetTop(RangeBottom, h - RangeBottom.Height);

        HandleIn.Height = h;
        Canvas.SetLeft(HandleIn, xIn - HandleWidth);
        Canvas.SetTop(HandleIn, 0);
        HandleOut.Height = h;
        Canvas.SetLeft(HandleOut, xOut);
        Canvas.SetTop(HandleOut, 0);

        var visible = Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        HandleIn.Visibility = HandleOut.Visibility = RangeTop.Visibility = RangeBottom.Visibility = visible;

        PlacePlayhead();
    }

    void PlacePlayhead()
    {
        var x = XOf(Position);
        var total = Root.ActualHeight;
        PlayheadLine.Height = Math.Max(0, total);
        Canvas.SetLeft(PlayheadLine, x - 1);
        Canvas.SetTop(PlayheadLine, 0);
        Canvas.SetLeft(PlayheadHead, x - 6);
        PlayheadLine.Visibility = PlayheadHead.Visibility = Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
    }

    static readonly double[] Steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];

    void DrawRuler()
    {
        RulerCanvas.Children.Clear();
        var w = RulerCanvas.ActualWidth;
        var dur = Duration.TotalSeconds;
        if (w <= 0 || dur <= 0) return;

        var pxPerSec = w / dur;
        var step = Steps.FirstOrDefault(s => s * pxPerSec >= 72, Steps[^1]);
        var minor = step / (step >= 60 ? 4 : 5);
        var tick = FindBrush("CB.TextTertiary", Brushes.Gray);
        var label = FindBrush("CB.TextSecondary", Brushes.LightGray);

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (var t = 0.0; t <= dur + 1e-6; t += minor)
            {
                var x = Math.Round(t * pxPerSec) + 0.5;
                if (x > w) break;
                var major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
                g.BeginFigure(new Point(x, 22), false, false);
                g.LineTo(new Point(x, major ? 13 : 18), true, false);
            }
        }
        geo.Freeze();
        RulerCanvas.Children.Add(new Path { Data = geo, Stroke = tick, StrokeThickness = 1 });

        for (var t = 0.0; t <= dur + 1e-6; t += step)
        {
            var x = t * pxPerSec;
            if (x + 34 > w && t > 0) break;
            var tb = new TextBlock
            {
                Text = FormatTick(TimeSpan.FromSeconds(t), step),
                FontSize = 10.5,
                Foreground = label,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            };
            Canvas.SetLeft(tb, x + 4);
            Canvas.SetTop(tb, -1);
            RulerCanvas.Children.Add(tb);
        }
    }

    Brush FindBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    static string FormatTick(TimeSpan t, double step)
    {
        if (step < 1) return $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    public static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var cs = t.Milliseconds / 10;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{cs:00}"
            : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{cs:00}";
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Duration <= TimeSpan.Zero) return;
        var x = e.GetPosition(TrackArea).X;

        if (HandleIn.IsMouseOver) _drag = DragMode.In;
        else if (HandleOut.IsMouseOver) _drag = DragMode.Out;
        else
        {
            _drag = DragMode.Seek;
            SeekRequested?.Invoke(this, TimeAt(x));
        }
        _rangeDirty = false;
        Root.CaptureMouse();
        ScrubbingChanged?.Invoke(this, true);
        Root.Focus();
        e.Handled = true;
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (Duration <= TimeSpan.Zero) return;
        var x = e.GetPosition(TrackArea).X;
        var t = TimeAt(x);

        switch (_drag)
        {
            case DragMode.Seek:
                SeekRequested?.Invoke(this, t);
                break;
            case DragMode.In:
            {
                var max = OutPoint - MinRange;
                InPoint = t > max ? max : t;
                _rangeDirty = true;
                SeekRequested?.Invoke(this, InPoint);
                break;
            }
            case DragMode.Out:
            {
                var min = InPoint + MinRange;
                OutPoint = t < min ? min : t;
                _rangeDirty = true;
                SeekRequested?.Invoke(this, OutPoint);
                break;
            }
        }
        SetHover(_drag == DragMode.None ? t : null);
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None) return;
        Root.ReleaseMouseCapture();
        EndDrag();
    }

    void EndDrag()
    {
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        if (_rangeDirty) RangeChanged?.Invoke(this, EventArgs.Empty);
        _rangeDirty = false;
        ScrubbingChanged?.Invoke(this, false);
    }

    void SetHover(TimeSpan? t)
    {
        if (t is null || Duration <= TimeSpan.Zero)
        {
            HoverLine.Visibility = HoverLabel.Visibility = Visibility.Collapsed;
            return;
        }
        var x = XOf(t.Value);
        HoverLine.Height = TrackArea.ActualHeight;
        Canvas.SetLeft(HoverLine, x);
        HoverLine.Visibility = Visibility.Visible;

        HoverText.Text = FormatTime(t.Value);
        HoverLabel.Visibility = Visibility.Visible;
        HoverLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var lw = HoverLabel.DesiredSize.Width;
        Canvas.SetLeft(HoverLabel, Math.Clamp(x - lw / 2, -HandleWidth, Math.Max(-HandleWidth, W + HandleWidth - lw)));
    }
}

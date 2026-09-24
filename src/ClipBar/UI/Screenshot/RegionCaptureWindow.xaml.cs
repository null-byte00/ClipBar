using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ClipBar.Capture;
using ClipBar.Core;
using ClipBar.UI.Overlay;
using WPath = System.Windows.Shapes.Path;

namespace ClipBar.UI.Screenshot;

public partial class RegionCaptureWindow : Window
{
    static RegionCaptureWindow? _open;

    enum Tool { Move, Pen, Marker, Line, Arrow, Rect, Text, Blur }
    enum Drag { None, NewSelection, MoveSelection, Resize, Draw }

    const double HandleGrab = 8;

    readonly BitmapSource _frozen;
    readonly double _scale;
    readonly string _label;

    Rect _sel = Rect.Empty;
    Drag _drag;
    Point _dragStart;
    Rect _selAtDragStart;
    int _resizeEdges;

    Tool _tool = Tool.Move;
    Color _color = Color.FromRgb(0xFF, 0x2D, 0x3F);
    double _size = 8;

    FrameworkElement? _drawing;
    Point _drawStart;
    TextBox? _editingText;
    readonly Stack<UIElement> _undo = new();
    bool _done;

    RegionCaptureWindow(BitmapSource frozen, double scale, string label)
    {
        InitializeComponent();
        _frozen = frozen; _scale = scale; _label = label;
        Frozen.Source = frozen;
        Root.SizeChanged += (_, e) => DimAll.Rect = new Rect(e.NewSize);
        DimHole.Rect = Rect.Empty;
    }

    public static void Start()
    {
        if (_open is { } existing) { existing.Close(); return; }
        try
        {
            var label = ForegroundApp.GetLabel();
            var (bounds, scale) = OverlayNative.GetMonitorUnderCursor();
            var frozen = ScreenGrabber.CaptureBitmap(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            var w = new RegionCaptureWindow(frozen, scale, label)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = bounds.Left / scale, Top = bounds.Top / scale,
                Width = bounds.Width / scale, Height = bounds.Height / scale,
            };
            _open = w;
            w.Closed += (_, _) => _open = null;
            w.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                OverlayNative.MakeToolWindow(hwnd);
                if (OverlayWindow.ExcludeFromCapture)
                    OverlayNative.SetWindowDisplayAffinity(hwnd, OverlayNative.WDA_EXCLUDEFROMCAPTURE);
            };
            w.Show();
            var h = new WindowInteropHelper(w).Handle;
            OverlayNative.SetWindowPos(h, OverlayNative.HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height, OverlayNative.SWP_SHOWWINDOW);
            OverlayNative.ForceForeground(h);
            w.Activate();
            Keyboard.Focus(w);
        }
        catch (Exception ex)
        {
            Log.Error("Region screenshot failed to start", ex);
            AppServices.Notifier?.Show("Не удалось сделать скриншот", ex.Message, NotifyKind.Error);
        }
    }

    void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(Root);
        CommitText();

        if (!_sel.IsEmpty && _tool != Tool.Move && _sel.Contains(p))
        {
            BeginDraw(p);
        }
        else if (!_sel.IsEmpty && _tool == Tool.Move && HitEdges(p) is var edges and > 0)
        {
            _drag = Drag.Resize; _resizeEdges = edges;
        }
        else if (!_sel.IsEmpty && _tool == Tool.Move && _sel.Contains(p))
        {
            _drag = Drag.MoveSelection;
        }
        else if (_tool != Tool.Move && !_sel.IsEmpty)
        {
            return;
        }
        else
        {
            if (_undo.Count > 0) { Ink.Children.Clear(); _undo.Clear(); }
            _drag = Drag.NewSelection;
            _sel = new Rect(p, p);
            Toolbar.Visibility = Visibility.Collapsed;
            HelpBadge.Visibility = Visibility.Collapsed;
        }
        _dragStart = p;
        _selAtDragStart = _sel;
        Root.CaptureMouse();
        UpdateSelectionVisuals();
        e.Handled = true;
    }

    void Root_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Root);
        switch (_drag)
        {
            case Drag.NewSelection:
                _sel = new Rect(_dragStart, p);
                UpdateSelectionVisuals();
                break;
            case Drag.MoveSelection:
                var d = p - _dragStart;
                var x = Math.Clamp(_selAtDragStart.X + d.X, 0, ActualWidth - _selAtDragStart.Width);
                var y = Math.Clamp(_selAtDragStart.Y + d.Y, 0, ActualHeight - _selAtDragStart.Height);
                _sel = new Rect(x, y, _selAtDragStart.Width, _selAtDragStart.Height);
                UpdateSelectionVisuals(); PlaceToolbar();
                break;
            case Drag.Resize:
                _sel = ResizedSelection(p);
                UpdateSelectionVisuals(); PlaceToolbar();
                break;
            case Drag.Draw:
                ContinueDraw(ClampToSel(p));
                break;
            default:
                UpdateHoverCursor(p);
                break;
        }
    }

    void Root_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Root.ReleaseMouseCapture();
        var drag = _drag;
        _drag = Drag.None;
        switch (drag)
        {
            case Drag.NewSelection:
                if (_sel.Width < 4 || _sel.Height < 4)
                {
                    _sel = Rect.Empty; UpdateSelectionVisuals(); HelpBadge.Visibility = Visibility.Visible;
                    return;
                }
                Toolbar.Visibility = Visibility.Visible;
                PlaceToolbar();
                break;
            case Drag.Draw:
                FinishDraw();
                break;
        }
    }

    void Root_MouseRightButtonUp(object sender, MouseButtonEventArgs e) => Close();

    int HitEdges(Point p)
    {
        var outer = _sel; outer.Inflate(HandleGrab, HandleGrab);
        if (!outer.Contains(p)) return 0;
        var e = 0;
        if (Math.Abs(p.X - _sel.Left) <= HandleGrab) e |= 1;
        if (Math.Abs(p.Y - _sel.Top) <= HandleGrab) e |= 2;
        if (Math.Abs(p.X - _sel.Right) <= HandleGrab) e |= 4;
        if (Math.Abs(p.Y - _sel.Bottom) <= HandleGrab) e |= 8;
        return e;
    }

    Rect ResizedSelection(Point p)
    {
        double l = _selAtDragStart.Left, t = _selAtDragStart.Top, r = _selAtDragStart.Right, b = _selAtDragStart.Bottom;
        p = new Point(Math.Clamp(p.X, 0, ActualWidth), Math.Clamp(p.Y, 0, ActualHeight));
        if ((_resizeEdges & 1) != 0) l = p.X;
        if ((_resizeEdges & 2) != 0) t = p.Y;
        if ((_resizeEdges & 4) != 0) r = p.X;
        if ((_resizeEdges & 8) != 0) b = p.Y;
        return new Rect(new Point(l, t), new Point(r, b));
    }

    void UpdateHoverCursor(Point p)
    {
        if (_sel.IsEmpty) { Cursor = Cursors.Cross; return; }
        if (_tool != Tool.Move) { Cursor = _sel.Contains(p) ? (_tool == Tool.Text ? Cursors.IBeam : Cursors.Pen) : Cursors.Arrow; return; }
        Cursor = HitEdges(p) switch
        {
            3 or 12 => Cursors.SizeNWSE,
            6 or 9 => Cursors.SizeNESW,
            1 or 4 => Cursors.SizeWE,
            2 or 8 => Cursors.SizeNS,
            _ => _sel.Contains(p) ? Cursors.SizeAll : Cursors.Cross,
        };
    }

    Point ClampToSel(Point p) => new(Math.Clamp(p.X, _sel.Left, _sel.Right), Math.Clamp(p.Y, _sel.Top, _sel.Bottom));

    Brush InkBrush(double alpha = 1)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), _color.R, _color.G, _color.B));
        b.Freeze();
        return b;
    }

    double Stroke => _size / 2.5 + 1;

    void BeginDraw(Point p)
    {
        _drawStart = p;
        switch (_tool)
        {
            case Tool.Text:
                PlaceTextBox(p);
                return;
            case Tool.Pen:
                _drawing = new Polyline { Stroke = InkBrush(), StrokeThickness = Stroke, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Points = [p] };
                break;
            case Tool.Marker:
                _drawing = new Polyline { Stroke = InkBrush(0.38), StrokeThickness = Stroke * 4, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Square, StrokeEndLineCap = PenLineCap.Square, Points = [p] };
                break;
            case Tool.Line:
                _drawing = new Line { Stroke = InkBrush(), StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y };
                break;
            case Tool.Arrow:
                _drawing = new WPath { Stroke = InkBrush(), StrokeThickness = Stroke, Fill = InkBrush(), StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round };
                break;
            case Tool.Rect:
                _drawing = new Rectangle { Stroke = InkBrush(), StrokeThickness = Stroke, RadiusX = 2, RadiusY = 2 };
                break;
            case Tool.Blur:
                _drawing = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = [4, 3], Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) };
                break;
            default: return;
        }
        Ink.Children.Add(_drawing);
        _drag = Drag.Draw;
        ContinueDraw(p);
    }

    void ContinueDraw(Point p)
    {
        switch (_drawing)
        {
            case Polyline line: line.Points.Add(p); break;
            case Line l: l.X2 = p.X; l.Y2 = p.Y; break;
            case WPath path: path.Data = ArrowGeometry(_drawStart, p, Stroke); break;
            case Rectangle rect:
                var r = new Rect(_drawStart, p);
                Canvas.SetLeft(rect, r.X); Canvas.SetTop(rect, r.Y); rect.Width = r.Width; rect.Height = r.Height;
                break;
        }
    }

    void FinishDraw()
    {
        if (_drawing is null) return;
        if (_tool == Tool.Blur && _drawing is Rectangle marquee)
        {
            var r = new Rect(Canvas.GetLeft(marquee), Canvas.GetTop(marquee), marquee.Width, marquee.Height);
            Ink.Children.Remove(marquee);
            _drawing = null;
            if (r.Width >= 3 && r.Height >= 3 && Pixelate(r) is { } img)
            {
                Ink.Children.Add(img);
                _undo.Push(img);
            }
            return;
        }
        _undo.Push(_drawing);
        _drawing = null;
    }

    Image? Pixelate(Rect r)
    {
        var px = new Int32Rect((int)(r.X * _scale), (int)(r.Y * _scale), (int)(r.Width * _scale), (int)(r.Height * _scale));
        px.Width = Math.Min(px.Width, _frozen.PixelWidth - px.X);
        px.Height = Math.Min(px.Height, _frozen.PixelHeight - px.Y);
        if (px.Width < 2 || px.Height < 2) return null;
        var block = Math.Max(6, _size * 1.5 * _scale);
        var small = new TransformedBitmap(new CroppedBitmap(_frozen, px), new ScaleTransform(1 / block, 1 / block));
        small.Freeze();
        var img = new Image { Source = small, Stretch = Stretch.Fill, Width = r.Width, Height = r.Height };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(img, r.X); Canvas.SetTop(img, r.Y);
        return img;
    }

    static Geometry ArrowGeometry(Point a, Point b, double thickness)
    {
        var v = b - a;
        var len = v.Length;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a, false, false);
            ctx.LineTo(b, true, true);
            if (len > 4)
            {
                v /= len;
                var head = Math.Min(10 + thickness * 3, len * 0.5);
                var n = new Vector(-v.Y, v.X);
                var basePt = b - v * head;
                ctx.BeginFigure(b, true, true);
                ctx.LineTo(basePt + n * head * 0.5, true, true);
                ctx.LineTo(basePt - n * head * 0.5, true, true);
            }
        }
        g.Freeze();
        return g;
    }

    void PlaceTextBox(Point p)
    {
        var box = new TextBox
        {
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Foreground = InkBrush(),
            CaretBrush = InkBrush(),
            FontSize = 12 + _size * 1.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = FontFamily,
            MinWidth = 40,
            AcceptsReturn = false,
            Padding = new Thickness(2, 0, 2, 0),
        };
        Canvas.SetLeft(box, p.X);
        Canvas.SetTop(box, p.Y - box.FontSize * 0.7);
        Ink.Children.Add(box);
        _editingText = box;
        box.LostKeyboardFocus += (_, _) => CommitText();
        Dispatcher.BeginInvoke(() => { box.Focus(); Keyboard.Focus(box); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    void CommitText()
    {
        var box = _editingText;
        if (box is null) return;
        _editingText = null;
        Ink.Children.Remove(box);
        if (string.IsNullOrWhiteSpace(box.Text)) { Keyboard.Focus(this); return; }
        var label = new TextBlock
        {
            Text = box.Text,
            Foreground = box.Foreground,
            FontSize = box.FontSize,
            FontWeight = box.FontWeight,
            FontFamily = box.FontFamily,
            Padding = new Thickness(3, 1, 3, 1),
        };
        label.Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 4, Opacity = 0.9, Color = Colors.Black };
        Canvas.SetLeft(label, Canvas.GetLeft(box));
        Canvas.SetTop(label, Canvas.GetTop(box));
        Ink.Children.Add(label);
        _undo.Push(label);
        Keyboard.Focus(this);
    }

    void UpdateSelectionVisuals()
    {
        if (_sel.IsEmpty || _sel.Width < 1 || _sel.Height < 1)
        {
            DimHole.Rect = Rect.Empty;
            SelBorder.Visibility = SizeBadge.Visibility = Visibility.Collapsed;
            return;
        }
        DimHole.Rect = _sel;
        SelBorder.Visibility = SizeBadge.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelBorder, _sel.X - 0.5); Canvas.SetTop(SelBorder, _sel.Y - 0.5);
        SelBorder.Width = _sel.Width + 1; SelBorder.Height = _sel.Height + 1;
        SizeText.Text = $"{Math.Round(_sel.Width * _scale)} × {Math.Round(_sel.Height * _scale)}";
        Canvas.SetLeft(SizeBadge, _sel.X);
        Canvas.SetTop(SizeBadge, _sel.Y > 26 ? _sel.Y - 26 : _sel.Y + 4);
    }

    void PlaceToolbar()
    {
        if (Toolbar.Visibility != Visibility.Visible) return;
        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Toolbar.DesiredSize;
        var x = Math.Clamp(_sel.Right - size.Width, 4, Math.Max(4, ActualWidth - size.Width - 4));
        var y = _sel.Bottom + 8 + size.Height < ActualHeight ? _sel.Bottom + 8
              : _sel.Top - 8 - size.Height > 0 ? _sel.Top - 8 - size.Height
              : _sel.Bottom - size.Height - 8;
        Canvas.SetLeft(Toolbar, x);
        Canvas.SetTop(Toolbar, y);
    }

    void Toolbar_PreviewMouseDown(object sender, MouseButtonEventArgs e) => CommitText();
    void Toolbar_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    void Tool_Checked(object sender, RoutedEventArgs e)
    {
        _tool = sender switch
        {
            _ when ReferenceEquals(sender, PenTool) => Tool.Pen,
            _ when ReferenceEquals(sender, MarkerTool) => Tool.Marker,
            _ when ReferenceEquals(sender, LineTool) => Tool.Line,
            _ when ReferenceEquals(sender, ArrowTool) => Tool.Arrow,
            _ when ReferenceEquals(sender, RectTool) => Tool.Rect,
            _ when ReferenceEquals(sender, TextTool) => Tool.Text,
            _ when ReferenceEquals(sender, BlurTool) => Tool.Blur,
            _ => Tool.Move,
        };
    }

    void Color_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is Control { Background: SolidColorBrush b }) _color = b.Color;
        if (_editingText is { } box) { box.Foreground = InkBrush(); box.CaretBrush = InkBrush(); }
    }

    void Size_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && double.TryParse(t, out var s)) _size = s;
    }

    void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    void Undo()
    {
        if (_editingText is not null) { Ink.Children.Remove(_editingText); _editingText = null; Keyboard.Focus(this); return; }
        if (_undo.Count > 0) Ink.Children.Remove(_undo.Pop());
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (_editingText is not null)
        {
            if (e.Key == Key.Enter) { CommitText(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Undo(); e.Handled = true; }
            return;
        }
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.C when ctrl: Finish(save: false); break;
            case Key.S when ctrl && (Keyboard.Modifiers & ModifierKeys.Shift) != 0: SaveAs(); break;
            case Key.S when ctrl: Finish(save: true); break;
            case Key.Enter: Finish(save: true); break;
            case Key.Z when ctrl: Undo(); break;
            default: return;
        }
        e.Handled = true;
    }

    void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();

    void SaveAs()
    {
        CommitText();
        if (_done || _sel.IsEmpty || _sel.Width < 4 || _sel.Height < 4) return;
        try
        {
            var image = RenderSelection();
            var name = $"{_label} {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
            foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить скриншот",
                FileName = name,
                Filter = "PNG (без потерь)|*.png|JPEG (меньше размер)|*.jpg",
                DefaultExt = ".png",
                AddExtension = true,
                InitialDirectory = AppServices.Settings?.Current.ClipsFolder is { } f && Directory.Exists(f) ? f : null,
            };
            if (dlg.ShowDialog(this) != true) return;

            BitmapEncoder enc = System.IO.Path.GetExtension(dlg.FileName).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || System.IO.Path.GetExtension(dlg.FileName).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(image));
            using (var fs = new FileStream(dlg.FileName, FileMode.Create, FileAccess.Write)) enc.Save(fs);
            CopyToClipboard(image);
            _done = true;
            Close();

            var inClips = AppServices.Settings?.Current.ClipsFolder is { } clips &&
                          string.Equals(System.IO.Path.GetDirectoryName(dlg.FileName)?.TrimEnd('\\'), clips.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (inClips) _ = AppServices.Library?.AddAsync(dlg.FileName);
            AppServices.Notifier?.Show("Скриншот сохранён", System.IO.Path.GetFileName(dlg.FileName), NotifyKind.Success, inClips ? dlg.FileName : null);
        }
        catch (Exception ex)
        {
            Log.Error("Region screenshot save-as failed", ex);
            AppServices.Notifier?.Show("Не удалось сохранить скриншот", ex.Message, NotifyKind.Error);
        }
    }

    void Copy_Click(object sender, RoutedEventArgs e) => Finish(save: false);
    void Save_Click(object sender, RoutedEventArgs e) => Finish(save: true);
    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void Finish(bool save)
    {
        CommitText();
        if (_done || _sel.IsEmpty || _sel.Width < 4 || _sel.Height < 4) return;
        _done = true;
        try
        {
            var image = RenderSelection();
            CopyToClipboard(image);
            var path = save ? SavePng(image) : null;
            Close();

            if (path is not null)
            {
                _ = AppServices.Library?.AddAsync(path);
                AppServices.Notifier?.Show("Скриншот сохранён", "И скопирован в буфер обмена", NotifyKind.Success, path);
            }
            else AppServices.Notifier?.Show("Скопировано в буфер обмена", $"{image.PixelWidth} × {image.PixelHeight} — вставь через Ctrl+V", NotifyKind.Success);
        }
        catch (Exception ex)
        {
            _done = false;
            Log.Error("Region screenshot failed", ex);
            AppServices.Notifier?.Show("Не удалось сделать скриншот", ex.Message, NotifyKind.Error);
        }
    }

    BitmapSource RenderSelection()
    {
        var pxW = (int)Math.Round(ActualWidth * _scale);
        var pxH = (int)Math.Round(ActualHeight * _scale);
        BitmapSource full;
        if (Ink.Children.Count == 0 && _frozen.PixelWidth == pxW && _frozen.PixelHeight == pxH)
        {
            full = _frozen;
        }
        else
        {
            var rtb = new RenderTargetBitmap(pxW, pxH, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
            rtb.Render(Composite);
            full = rtb;
        }
        var crop = new Int32Rect(
            (int)Math.Round(_sel.X * _scale), (int)Math.Round(_sel.Y * _scale),
            (int)Math.Round(_sel.Width * _scale), (int)Math.Round(_sel.Height * _scale));
        crop.Width = Math.Min(crop.Width, full.PixelWidth - crop.X);
        crop.Height = Math.Min(crop.Height, full.PixelHeight - crop.Y);
        var cropped = new CroppedBitmap(full, crop);
        cropped.Freeze();
        return cropped;
    }

    static void CopyToClipboard(BitmapSource image)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { Clipboard.SetImage(image); return; }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5) { Thread.Sleep(60); }
        }
    }

    string SavePng(BitmapSource image)
    {
        var folder = AppServices.Settings?.Current.ClipsFolder
                     ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ClipBar");
        Directory.CreateDirectory(folder);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        var useLabel = AppServices.Settings?.Current.UseAppNameInFileName ?? true;
        var name = useLabel ? $"{_label} {stamp}" : stamp;
        foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');

        var path = System.IO.Path.Combine(folder, name + ".png");
        for (var n = 2; File.Exists(path); n++) path = System.IO.Path.Combine(folder, $"{name} ({n}).png");

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        enc.Save(fs);
        return path;
    }
}

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClipBar.Core;
using ClipBar.Library;

namespace ClipBar.UI.Controls;

public static class GalleryThumb
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.RegisterAttached(
        "SourcePath", typeof(string), typeof(GalleryThumb), new PropertyMetadata(null, OnSourceChanged));
    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(GalleryThumb), new PropertyMetadata(320));
    public static readonly DependencyProperty HasImageProperty = DependencyProperty.RegisterAttached(
        "HasImage", typeof(bool), typeof(GalleryThumb), new PropertyMetadata(false));

    public static string? GetSourcePath(DependencyObject d) => (string?)d.GetValue(SourcePathProperty);
    public static void SetSourcePath(DependencyObject d, string? v) => d.SetValue(SourcePathProperty, v);
    public static int GetDecodeWidth(DependencyObject d) => (int)d.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject d, int v) => d.SetValue(DecodeWidthProperty, v);
    public static bool GetHasImage(DependencyObject d) => (bool)d.GetValue(HasImageProperty);
    public static void SetHasImage(DependencyObject d, bool v) => d.SetValue(HasImageProperty, v);

    const int CacheCapacity = 160;
    static readonly object CacheGate = new();
    static readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Bmp)>> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly LinkedList<(string Key, BitmapSource Bmp)> Lru = new();

    static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Shape shape) return;
        var path = e.NewValue as string;
        if (string.IsNullOrEmpty(path))
        {
            shape.Fill = null;
            SetHasImage(shape, false);
            return;
        }
        var width = GetDecodeWidth(shape);
        var key = $"{path}|{width}";
        if (TryGetCached(key, out var cached))
        {
            Apply(shape, cached);
            return;
        }
        SetHasImage(shape, false);
        var dispatcher = shape.Dispatcher;
        _ = Task.Run(() =>
        {
            var bmp = Decode(path, width);
            if (bmp is null) return;
            Put(key, bmp);
            dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                if (string.Equals(GetSourcePath(shape), path, StringComparison.OrdinalIgnoreCase)) Apply(shape, bmp);
            });
        });
    }

    static void Apply(Shape shape, BitmapSource bmp)
    {
        var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
        brush.Freeze();
        shape.Fill = brush;
        SetHasImage(shape, true);
    }

    public static BitmapSource? Decode(string path, int decodeWidth)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"Thumbnail decode failed ({System.IO.Path.GetFileName(path)}): {ex.Message}");
            return null;
        }
    }

    static bool TryGetCached(string key, out BitmapSource bmp)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var node))
            {
                Lru.Remove(node); Lru.AddFirst(node);
                bmp = node.Value.Bmp; return true;
            }
        }
        bmp = null!; return false;
    }

    static void Put(string key, BitmapSource bmp)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var existing)) { Lru.Remove(existing); Cache.Remove(key); }
            var node = new LinkedListNode<(string, BitmapSource)>((key, bmp));
            Lru.AddFirst(node); Cache[key] = node;
            while (Cache.Count > CacheCapacity)
            {
                var last = Lru.Last!; Lru.RemoveLast(); Cache.Remove(last.Value.Key);
            }
        }
    }

    public static void Invalidate(string path)
    {
        lock (CacheGate)
        {
            var dead = Cache.Keys.Where(k => k.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var k in dead) { Lru.Remove(Cache[k]); Cache.Remove(k); }
        }
    }
}

public sealed class GalleryDurationConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => ClipFormat.Duration(value as TimeSpan?);
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class GalleryMetaConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is ClipInfo clip ? ClipFormat.Meta(clip) : "";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class GalleryDayGroupConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is DateTime d ? ClipFormat.DayGroup(d) : "";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class GalleryVisibleWhenConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var truthy = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            TimeSpan ts => ts > TimeSpan.Zero,
            _ => true,
        };
        if (p is string ps && ps.Equals("invert", StringComparison.OrdinalIgnoreCase)) truthy = !truthy;
        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

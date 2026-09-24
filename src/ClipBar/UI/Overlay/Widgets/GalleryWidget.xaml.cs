using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipBar.Core;
using Wpf.Ui.Controls;

namespace ClipBar.UI.Overlay.Widgets;

public sealed class GalleryTile : INotifyPropertyChanged
{
    public required ClipInfo Clip { get; init; }
    public string Path => Clip.FilePath;
    public string Title => Clip.Title;
    public bool IsScreenshot => Clip.IsScreenshot;

    ImageSource? _thumb; string _badge = "";
    public ImageSource? Thumb { get => _thumb; set { if (!ReferenceEquals(_thumb, value)) { _thumb = value; Raise(); Raise(nameof(ShowPlaceholder)); } } }
    public string Badge { get => _badge; set { if (_badge != value) { _badge = value; Raise(); Raise(nameof(HasBadge)); } } }
    public bool HasBadge => IsScreenshot || _badge.Length > 0;
    public bool ShowPlaceholder => _thumb is null;
    public string Tooltip => IsScreenshot ? $"{Clip.FileName}\nСкриншот · {Clip.CreatedAt:g}" : $"{Clip.FileName}\n{Clip.CreatedAt:g} · открыть в редакторе";

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class GalleryWidget : UserControl, IOverlayWidget
{
    public string Id => "gallery";
    public string Title => "Коллекция";
    public SymbolRegular Icon => SymbolRegular.ImageMultiple24;
    public IOverlayHost? Host { get; set; }
    public bool NeedsFastTick => false;

    const int MaxTiles = 6;

    public bool? Screenshots { get; init; }
    readonly ObservableCollection<GalleryTile> _tiles = [];
    readonly List<ClipInfo> _watched = [];
    static readonly Dictionary<string, ImageSource> ThumbCache = new(StringComparer.OrdinalIgnoreCase);
    bool _active;

    public GalleryWidget()
    {
        InitializeComponent();
        Tiles.ItemsSource = _tiles;
    }

    static IClipLibrary? Library => AppServices.Library;

    public void Activate()
    {
        if (_active) return;
        _active = true;
        if (Library?.Items is { } items) items.CollectionChanged += OnItemsChanged;
        Rebuild();
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        if (Library?.Items is { } items) items.CollectionChanged -= OnItemsChanged;
        Unwatch();
    }

    public void FastTick() { }
    public void SlowTick() { }

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess()) Rebuild(); else Dispatcher.BeginInvoke(Rebuild);
    }

    void Unwatch()
    {
        foreach (var c in _watched) c.PropertyChanged -= OnClipChanged;
        _watched.Clear();
    }

    void OnClipChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ClipInfo clip) return;
        Dispatcher.BeginInvoke(() =>
        {
            var tile = _tiles.FirstOrDefault(t => ReferenceEquals(t.Clip, clip));
            if (tile is null) return;
            if (e.PropertyName is nameof(ClipInfo.Duration) or null) tile.Badge = BadgeFor(clip);
            if (e.PropertyName is nameof(ClipInfo.ThumbnailPath) or null) LoadThumb(tile);
        });
    }

    void Rebuild()
    {
        if (!_active) return;
        try
        {
            Unwatch();
            _tiles.Clear();
            var items = Library?.Items;
            if (items is not null)
            {
                foreach (var clip in items.Where(c => Screenshots is not { } s || c.IsScreenshot == s).Take(MaxTiles))
                {
                    var tile = new GalleryTile { Clip = clip, Badge = BadgeFor(clip) };
                    _tiles.Add(tile);
                    clip.PropertyChanged += OnClipChanged;
                    _watched.Add(clip);
                    LoadThumb(tile);
                }
            }
            var empty = _tiles.Count == 0;
            EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            Tiles.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            if (empty)
            {
                EmptyTitle.Text = Screenshots == true ? "Пока нет скриншотов" : Screenshots == false ? "Пока нет клипов" : "Пока нет записей";
                EmptyHint.Text = Screenshots == true
                    ? $"Сделай скриншот ({Actions.HotkeyText(HotkeyAction.RegionScreenshot)} — область, {Actions.HotkeyText(HotkeyAction.Screenshot)} — весь экран) — он появится здесь."
                    : Screenshots == false
                    ? $"Сохрани откат ({Actions.HotkeyText(HotkeyAction.SaveReplay)}) или запись ({Actions.HotkeyText(HotkeyAction.ToggleRecording)}) — клип появится здесь."
                    : $"Сохрани откат ({Actions.HotkeyText(HotkeyAction.SaveReplay)}) или сделай скриншот ({Actions.HotkeyText(HotkeyAction.Screenshot)}) — они появятся здесь.";
            }
        }
        catch (Exception ex) { Log.Error("GalleryWidget rebuild failed", ex); }
    }

    static string BadgeFor(ClipInfo c)
    {
        if (c.IsScreenshot) return "";
        if (c.Duration is not { } d) return "";
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{(int)d.TotalMinutes}:{d.Seconds:00}";
    }

    void LoadThumb(GalleryTile tile)
    {
        var path = tile.Clip.ThumbnailPath;
        if (string.IsNullOrEmpty(path)) return;
        lock (ThumbCache)
        {
            if (ThumbCache.TryGetValue(path, out var cached)) { tile.Thumb = cached; return; }
        }
        _ = Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path)) return;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.DecodePixelWidth = 240;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.EndInit();
                bmp.Freeze();
                lock (ThumbCache)
                {
                    if (ThumbCache.Count > 48) ThumbCache.Clear();
                    ThumbCache[path] = bmp;
                }
                Dispatcher.BeginInvoke(() => { if (_tiles.Contains(tile)) tile.Thumb = bmp; });
            }
            catch (Exception ex) { Log.Warn($"Thumbnail load failed for {path}: {ex.Message}"); }
        });
    }

    void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GalleryTile tile }) return;
        try { AppServices.Shell?.OpenEditor(tile.Path); }
        catch (Exception ex)
        {
            Log.Error("OpenEditor failed", ex);
            AppServices.Notifier?.Show("Не удалось открыть клип", ex.Message, NotifyKind.Error);
        }
    }

    void OpenGallery_Click(object sender, RoutedEventArgs e)
    {
        if (Host is OverlayWindow overlay) { overlay.ShowGalleryView(); return; }
        try { AppServices.Shell?.ShowMain("gallery"); } catch (Exception ex) { Log.Error("ShowMain failed", ex); }
        Host?.HideOverlay();
    }
}

using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClipBar.Core;
using ClipBar.Library;
using ClipBar.UI.Controls;

namespace ClipBar.UI.Pages;

public partial class GalleryPage : Page
{
    enum Kind { All, Clips, Shots }

    readonly IClipLibrary? _lib;
    readonly ListCollectionView? _view;
    readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly DispatcherTimer _countsDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _playerTick = new() { Interval = TimeSpan.FromMilliseconds(250) };

    Kind _kind = Kind.All;
    string _search = "";
    ClipInfo? _selected;
    ClipInfo? _menuClip;
    bool _renaming, _renameCommitting;
    bool _playing, _mediaOpened, _seeking;
    DateTime _lastRefresh = DateTime.MinValue;

    public GalleryPage()
    {
        InitializeComponent();

        _lib = AppServices.Library;
        if (_lib is not null)
        {
            var cvs = (CollectionViewSource)Resources["ClipsView"];
            cvs.Source = _lib.Items;
            _view = (ListCollectionView)cvs.View;
            using (_view.DeferRefresh())
            {
                _view.SortDescriptions.Add(new SortDescription(nameof(ClipInfo.CreatedAt), ListSortDirection.Descending));
                _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ClipInfo.CreatedAt), (GalleryDayGroupConverter)Resources["DayGroup"]));
                _view.Filter = FilterClip;
            }
            _lib.Items.CollectionChanged += Items_CollectionChanged;
        }

        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplyFilter(); };
        _countsDebounce.Tick += (_, _) => { _countsDebounce.Stop(); UpdateCountsAndStates(); };
        _playerTick.Tick += (_, _) => UpdateTransport();
        if (_selected is not null) ShowDetail(_selected);
    }

    void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateEmptyHint();
            UpdateCountsAndStates();
            if (_lib is not null && DateTime.UtcNow - _lastRefresh > TimeSpan.FromSeconds(2))
            {
                _lastRefresh = DateTime.UtcNow;
                _ = _lib.RefreshAsync();
            }
        }
        catch (Exception ex) { Log.Error("GalleryPage load failed", ex); }
    }

    void Page_Unloaded(object sender, RoutedEventArgs e) => StopPreview();

    void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _countsDebounce.Stop(); _countsDebounce.Start();
        if (e.Action == NotifyCollectionChangedAction.Remove && _selected is not null && e.OldItems?.Contains(_selected) == true)
            ShowDetail(null);
        if (e.Action == NotifyCollectionChangedAction.Reset && _selected is not null && _lib?.Items.Contains(_selected) != true)
            ShowDetail(null);
    }

    void UpdateEmptyHint()
    {
        try
        {
            var settings = AppServices.Settings;
            var hotkey = settings is null ? "Alt+F10" : Actions.HotkeyText(HotkeyAction.SaveReplay);
            var shot = settings is null ? "Alt+F1" : Actions.HotkeyText(HotkeyAction.Screenshot);
            var len = settings is null ? "5 мин" : Actions.FormatDuration(TimeSpan.FromSeconds(settings.Current.ReplaySeconds));
            EmptyHint.Text = $"Нажми {hotkey}, чтобы сохранить последние {len} — клип сразу появится здесь. Скриншоты ({shot}) тоже попадают в коллекцию.";
        }
        catch { }
    }

    void UpdateCountsAndStates()
    {
        var items = _lib?.Items;
        if (items is null || items.Count == 0)
        {
            CountText.Text = "Клипы и скриншоты появятся здесь";
            List.Visibility = Visibility.Collapsed;
            NotFoundState.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            if (_selected is not null) ShowDetail(null);
            return;
        }
        int clips = 0, shots = 0; long bytes = 0;
        foreach (var c in items) { if (c.IsScreenshot) shots++; else clips++; bytes += c.SizeBytes; }
        var parts = new List<string>(3);
        if (clips > 0) parts.Add(ClipFormat.Plural(clips, "клип", "клипа", "клипов"));
        if (shots > 0) parts.Add(ClipFormat.Plural(shots, "скриншот", "скриншота", "скриншотов"));
        parts.Add(ClipFormat.Size(bytes));
        CountText.Text = string.Join(" · ", parts);

        var empty = _view?.IsEmpty ?? true;
        EmptyState.Visibility = Visibility.Collapsed;
        NotFoundState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    bool FilterClip(object o)
    {
        if (o is not ClipInfo c) return false;
        if (_kind == Kind.Clips && c.IsScreenshot) return false;
        if (_kind == Kind.Shots && !c.IsScreenshot) return false;
        return _search.Length == 0 || c.Title.Contains(_search, StringComparison.CurrentCultureIgnoreCase);
    }

    void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        _kind = ReferenceEquals(sender, FilterClips) ? Kind.Clips : ReferenceEquals(sender, FilterShots) ? Kind.Shots : Kind.All;
        ApplyFilter();
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop(); _searchDebounce.Start();
    }

    void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchBox.Text = ""; e.Handled = true; }
        if (e.Key == Key.Enter) { _searchDebounce.Stop(); ApplyFilter(); e.Handled = true; }
    }

    void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        FilterAll.IsChecked = true;
        _searchDebounce.Stop();
        ApplyFilter();
    }

    void ApplyFilter()
    {
        if (_view is null) return;
        try
        {
            _search = SearchBox.Text.Trim();
            _view.Refresh();
            UpdateCountsAndStates();
            if (_selected is not null && !FilterClip(_selected)) ShowDetail(null);
        }
        catch (Exception ex) { Log.Error("Gallery filter failed", ex); }
    }

    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_lib is null) return;
        _lastRefresh = DateTime.UtcNow;
        _ = _lib.RefreshAsync();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_lib is ClipLibrary lib) { lib.OpenFolder(); return; }
            var folder = AppServices.Settings?.Current.ClipsFolder;
            if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("OpenFolder failed", ex);
            AppServices.Notifier?.Show("Не удалось открыть папку", ex.Message, NotifyKind.Error);
        }
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail(List.SelectedItem as ClipInfo);

    void ShowDetail(ClipInfo? clip)
    {
        if (_renaming) CancelRename();
        StopPreview();
        _selected = clip;
        if (clip is null)
        {
            DetailPane.Visibility = Visibility.Collapsed;
            DetailPane.DataContext = null;
            if (List.SelectedItem is not null) List.SelectedItem = null;
            return;
        }
        DetailPane.DataContext = null;
        DetailPane.DataContext = clip;
        DetailPane.Visibility = Visibility.Visible;
        BigPlay.Visibility = clip.IsScreenshot ? Visibility.Collapsed : Visibility.Visible;
        Transport.Visibility = clip.IsScreenshot ? Visibility.Collapsed : Visibility.Visible;
        PreviewHost.Cursor = clip.IsScreenshot ? Cursors.Arrow : Cursors.Hand;
        PreviewPlaceholder.Symbol = clip.IsScreenshot ? Wpf.Ui.Controls.SymbolRegular.Image24 : Wpf.Ui.Controls.SymbolRegular.VideoClip24;
        Seek.Value = 0;
        TimeText.Text = $"00:00 / {Short(clip.Duration)}";
        UpdateMeta(clip);
        clip.PropertyChanged -= Selected_PropertyChanged;
        clip.PropertyChanged += Selected_PropertyChanged;
    }

    void Selected_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _selected)) { if (sender is ClipInfo c) c.PropertyChanged -= Selected_PropertyChanged; return; }
        UpdateMeta(_selected!);
        if (e.PropertyName == nameof(ClipInfo.Duration) && !_mediaOpened) TimeText.Text = $"00:00 / {Short(_selected!.Duration)}";
    }

    void UpdateMeta(ClipInfo c)
    {
        MetaDate.Text = ClipFormat.DateTimeLong(c.CreatedAt);
        var res = ClipFormat.Resolution(c.Width, c.Height);
        if (c.IsScreenshot)
        {
            MetaKindIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Image24;
            MetaKind.Text = string.IsNullOrEmpty(res) ? "Скриншот" : $"Скриншот · {res}";
            MetaAudioRow.Visibility = Visibility.Collapsed;
        }
        else
        {
            MetaKindIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Timer24;
            var dur = c.Duration is null ? "…" : ClipFormat.Duration(c.Duration);
            MetaKind.Text = string.IsNullOrEmpty(res) ? dur : $"{dur} · {res}";
            MetaAudioRow.Visibility = Visibility.Visible;
            MetaAudio.Text = c.Duration is null && c.AudioTracks == 0 ? "Читаю данные…" : ClipFormat.AudioTracks(c.AudioTracks);
        }
        MetaFile.Text = $"{ClipFormat.Size(c.SizeBytes)} · {Path.GetExtension(c.FilePath).TrimStart('.').ToUpperInvariant()}";
    }

    static string Short(TimeSpan? t)
    {
        if (t is null) return "--:--";
        var s = (long)Math.Round(t.Value.TotalSeconds);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
    }

    void Preview_Click(object sender, MouseButtonEventArgs e)
    {
        if (_selected is null || _selected.IsScreenshot) return;
        if (_playing) PausePreview(); else PlayPreview();
    }

    void PlayPreview()
    {
        if (_selected is null) return;
        try
        {
            if (!_mediaOpened)
            {
                Player.Source = new Uri(_selected.FilePath);
            }
            Player.IsMuted = false;
            Player.Volume = 0.8;
            Player.Play();
            _playing = true;
            VideoHost.Visibility = Visibility.Visible;
            BigPlay.Visibility = Visibility.Collapsed;
            _playerTick.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Preview play failed", ex);
            StopPreview();
        }
    }

    void PausePreview()
    {
        try { Player.Pause(); } catch { }
        _playing = false;
        BigPlay.Visibility = Visibility.Visible;
        BigPlayIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Play24;
    }

    void StopPreview()
    {
        _playerTick.Stop();
        _playing = false; _mediaOpened = false;
        try { Player.Stop(); Player.Close(); Player.Source = null; } catch { }
        VideoHost.Visibility = Visibility.Collapsed;
        BigPlay.Visibility = _selected is { IsScreenshot: false } ? Visibility.Visible : Visibility.Collapsed;
        BigPlayIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Play24;
        Seek.Value = 0;
    }

    void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _mediaOpened = true;
        if (Player.NaturalDuration.HasTimeSpan) Seek.Maximum = Math.Max(0.1, Player.NaturalDuration.TimeSpan.TotalSeconds);
    }

    void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        try { Player.Position = TimeSpan.Zero; Player.Pause(); } catch { }
        _playing = false;
        BigPlay.Visibility = Visibility.Visible;
        UpdateTransport();
    }

    void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Log.Warn("Preview MediaFailed: " + e.ErrorException.Message);
        StopPreview();
        AppServices.Notifier?.Show("Не удалось воспроизвести", "Открой клип во внешнем плеере", NotifyKind.Warning);
    }

    void UpdateTransport()
    {
        if (!_mediaOpened) return;
        var pos = Player.Position;
        var total = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : _selected?.Duration ?? TimeSpan.Zero;
        if (!_seeking) Seek.Value = Math.Min(Seek.Maximum, pos.TotalSeconds);
        TimeText.Text = $"{Short(pos)} / {Short(total)}";
    }

    void Seek_MouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _seeking = false;
        SeekTo(Seek.Value);
    }

    void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seeking) SeekTo(e.NewValue);
    }

    void SeekTo(double seconds)
    {
        if (_selected is null || _selected.IsScreenshot) return;
        try
        {
            if (!_mediaOpened) { PlayPreview(); }
            Player.Position = TimeSpan.FromSeconds(seconds);
            UpdateTransport();
        }
        catch (Exception ex) { Log.Warn("Seek failed: " + ex.Message); }
    }

    void VideoHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        VideoHost.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 8, 8);
    }

    void Card_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: ClipInfo c }) { OpenInEditor(c); e.Handled = true; }
    }

    void Card_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item) { item.IsSelected = true; item.Focus(); }
    }

    void ClipMenu_Opened(object sender, RoutedEventArgs e)
    {
        _menuClip = (sender as ContextMenu)?.PlacementTarget switch
        {
            ListBoxItem { DataContext: ClipInfo c } => c,
            FrameworkElement { DataContext: ClipInfo c } => c,
            _ => _selected,
        };
    }

    ClipInfo? MenuTarget => _menuClip ?? _selected;

    void Menu_Edit(object sender, RoutedEventArgs e) { if (MenuTarget is { } c) OpenInEditor(c); }
    void Menu_Play(object sender, RoutedEventArgs e) { if (MenuTarget is { } c) PlayExternal(c); }
    void Menu_ShowInFolder(object sender, RoutedEventArgs e) { if (MenuTarget is { } c) _lib?.ShowInExplorer(c); }
    void Menu_Rename(object sender, RoutedEventArgs e) { if (MenuTarget is { } c) { Select(c); BeginRename(); } }
    void Menu_Delete(object sender, RoutedEventArgs e) { if (MenuTarget is { } c) _ = DeleteWithConfirmAsync(c); }

    void Edit_Click(object sender, RoutedEventArgs e) { if (_selected is { } c) OpenInEditor(c); }
    void PlayExternal_Click(object sender, RoutedEventArgs e) { if (_selected is { } c) PlayExternal(c); }
    void ShowInFolder_Click(object sender, RoutedEventArgs e) { if (_selected is { } c) _lib?.ShowInExplorer(c); }
    void Rename_Click(object sender, RoutedEventArgs e) { if (_selected is not null) BeginRename(); }
    void Delete_Click(object sender, RoutedEventArgs e) { if (_selected is { } c) _ = DeleteWithConfirmAsync(c); }

    void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (_selected is null || _renaming) return;
        switch (e.Key)
        {
            case Key.Delete: _ = DeleteWithConfirmAsync(_selected); e.Handled = true; break;
            case Key.Enter: OpenInEditor(_selected); e.Handled = true; break;
            case Key.F2: BeginRename(); e.Handled = true; break;
            case Key.Space: if (!_selected.IsScreenshot) { if (_playing) PausePreview(); else PlayPreview(); e.Handled = true; } break;
        }
    }

    void Select(ClipInfo c)
    {
        if (!ReferenceEquals(List.SelectedItem, c)) List.SelectedItem = c;
        else if (!ReferenceEquals(_selected, c)) ShowDetail(c);
        List.ScrollIntoView(c);
    }

    void OpenInEditor(ClipInfo c)
    {
        try
        {
            StopPreview();
            if (c.IsScreenshot) { PlayExternal(c); return; }
            var shell = AppServices.Shell;
            if (shell is null) { AppServices.Notifier?.Show("Редактор недоступен", "", NotifyKind.Warning); return; }
            shell.OpenEditor(c.FilePath);
        }
        catch (Exception ex)
        {
            Log.Error("OpenEditor failed", ex);
            AppServices.Notifier?.Show("Не удалось открыть редактор", ex.Message, NotifyKind.Error);
        }
    }

    static void PlayExternal(ClipInfo c)
    {
        try
        {
            if (!File.Exists(c.FilePath)) { AppServices.Notifier?.Show("Файл не найден", c.FileName, NotifyKind.Warning); return; }
            Process.Start(new ProcessStartInfo(c.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Play external failed", ex);
            AppServices.Notifier?.Show("Не удалось открыть файл", ex.Message, NotifyKind.Error);
        }
    }

    void BeginRename()
    {
        if (_selected is null || _lib is null) return;
        StopPreview();
        _renaming = true;
        RenameBox.Text = _selected.Title;
        DetailTitle.Visibility = Visibility.Collapsed;
        RenameBox.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { RenameBox.Focus(); RenameBox.SelectAll(); });
    }

    void CancelRename()
    {
        _renaming = false;
        RenameBox.Visibility = Visibility.Collapsed;
        DetailTitle.Visibility = Visibility.Visible;
    }

    async void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.Enter) { e.Handled = true; await CommitRenameAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; CancelRename(); List.Focus(); }
        }
        catch (Exception ex) { Log.Error("Rename key failed", ex); }
    }

    async void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        try { if (_renaming) await CommitRenameAsync(); }
        catch (Exception ex) { Log.Error("Rename commit failed", ex); }
    }

    async Task CommitRenameAsync()
    {
        if (!_renaming || _renameCommitting || _selected is null || _lib is null) return;
        _renameCommitting = true;
        try
        {
            var clip = _selected;
            var newTitle = RenameBox.Text.Trim();
            CancelRename();
            if (newTitle.Length == 0 || newTitle == clip.Title) return;
            var oldPath = clip.FilePath;
            var ok = await _lib.RenameAsync(clip, newTitle);
            if (!ok)
            {
                AppServices.Notifier?.Show("Не удалось переименовать", "Проверь имя файла: возможно, он используется другой программой", NotifyKind.Error);
                return;
            }
            GalleryThumb.Invalidate(oldPath);
            if (ReferenceEquals(_selected, clip) || _selected is null)
            {
                _selected = null;
                List.SelectedItem = clip;
                if (!ReferenceEquals(List.SelectedItem, clip)) ShowDetail(clip);
            }
        }
        finally { _renameCommitting = false; }
    }

    async Task DeleteWithConfirmAsync(ClipInfo clip)
    {
        if (_lib is null) return;
        try
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = clip.IsScreenshot ? "Удалить скриншот?" : "Удалить клип?",
                Content = new TextBlock
                {
                    Text = $"«{clip.Title}» будет перемещён в корзину.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 380,
                },
                PrimaryButtonText = "Удалить",
                PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
                CloseButtonText = "Отмена",
            };
            try { if (Window.GetWindow(this) is { } owner) box.Owner = owner; } catch { }
            var result = await box.ShowDialogAsync();
            if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

            if (ReferenceEquals(clip, _selected)) StopPreview();
            var index = _view?.IndexOf(clip) ?? -1;
            await _lib.DeleteAsync(clip);
            GalleryThumb.Invalidate(clip.FilePath);
            AppServices.Notifier?.Show(clip.IsScreenshot ? "Скриншот удалён" : "Клип удалён", "Файл перемещён в корзину", NotifyKind.Info);

            if (_view is not null && _view.Count > 0)
            {
                var next = Math.Clamp(index, 0, _view.Count - 1);
                if (_view.GetItemAt(next) is ClipInfo n) { List.SelectedItem = n; List.ScrollIntoView(n); }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Delete failed", ex);
            AppServices.Notifier?.Show("Не удалось удалить", ex.Message, NotifyKind.Error);
        }
    }
}

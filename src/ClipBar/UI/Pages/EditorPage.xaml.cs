using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipBar.Core;
using ClipBar.Editor;
using ClipBar.UI.Controls;
using Symbol = Wpf.Ui.Controls.SymbolRegular;

namespace ClipBar.UI.Pages;

public partial class EditorPage : Page
{
    public sealed record PresetItem(ExportPreset Preset, Symbol Icon)
    {
        public string Title => Preset.Title;
        public string Subtitle => Preset.Subtitle;
    }

    static readonly PresetItem[] Presets =
    [
        new(ExportPreset.Get(ExportPresetKind.FastCopy), Symbol.Rocket24),
        new(ExportPreset.Get(ExportPresetKind.Precise), Symbol.Cut24),
        new(ExportPreset.Get(ExportPresetKind.Discord), Symbol.Chat24),
        new(ExportPreset.Get(ExportPresetKind.Vertical), Symbol.RectanglePortrait24),
        new(ExportPreset.Get(ExportPresetKind.Gif), Symbol.Gif24),
        new(ExportPreset.Get(ExportPresetKind.Mp3), Symbol.MusicNote224),
    ];

    public sealed record SpeedItem(double Value, string Label);

    static readonly SpeedItem[] Speeds =
    [
        new(0.25, "0,25×"),
        new(0.5, "0,5×"),
        new(1.0, "1×"),
        new(1.5, "1,5×"),
        new(2.0, "2×"),
        new(4.0, "4×"),
    ];

    const int NormalSpeedIndex = 2;

    static readonly TimeSpan MinRange = TimeSpan.FromMilliseconds(100);

    readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer _resizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(450) };

    string? _clipPath, _pendingClip;
    ClipMediaInfo? _info;
    TimeSpan _duration, _position, _in, _out;
    bool _playing, _mediaOpened, _scrubWasPlaying, _updatingAudioUi;
    CancellationTokenSource? _loadCts;
    PreviewAudioMixer? _mixer;
    ExportJob? _job;
    int _stripFrames;
    double _stripWidth;
    double _speed = 1.0;

    sealed record MediaCut(string Path, string Name, TimeSpan In, TimeSpan Out, bool HasAudio);
    readonly List<MediaCut> _cuts = new();

    public EditorPage()
    {
        _updatingAudioUi = true;
        InitializeComponent();
        _updatingAudioUi = false;
        PresetList.ItemsSource = Presets;
        PresetList.SelectedIndex = 0;
        SpeedList.ItemsSource = Speeds;
        SpeedList.SelectedIndex = NormalSpeedIndex;

        _timer.Tick += OnTick;
        _resizeDebounce.Tick += OnResizeDebounce;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnPreviewKeyDown;
        DragOver += OnDragOver;
        Drop += OnDrop;

        Timeline.SeekRequested += (_, t) => Seek(t);
        Timeline.RangeChanged += (_, _) => { _in = Timeline.InPoint; _out = Timeline.OutPoint; UpdateRangeUi(); };
        Timeline.ScrubbingChanged += OnScrubbing;
        Timeline.SizeChanged += (_, _) => { if (_info is not null) { _resizeDebounce.Stop(); _resizeDebounce.Start(); } };

        Media.MediaOpened += OnMediaOpened;
        Media.MediaFailed += OnMediaFailed;
        Media.MediaEnded += (_, _) => { Pause(); _position = _duration; Timeline.Position = _position; UpdateTimeLabel(); };

        ShowEmpty(true);
    }

    public string? CurrentClip => _clipPath ?? _pendingClip;

    public void LoadClip(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!IsLoaded) { _pendingClip = path; return; }
        _ = LoadClipAsync(path);
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_pendingClip is { } p) { _pendingClip = null; _ = LoadClipAsync(p); }
        else if (_info is not null)
        {
            _timer.Start();
            if (ExportJob.Current is { IsDone: false } running && _job != running && IsOurJob(running)) AttachJob(running);
        }
        Focus();
    }

    bool IsOurJob(ExportJob job) =>
        _clipPath is not null && string.Equals(job.Request.SourcePath, _clipPath, StringComparison.OrdinalIgnoreCase);

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Pause();
        _timer.Stop();
        _resizeDebounce.Stop();

        _loadCts?.Cancel();
        _mixer?.Dispose();
        _mixer = null;
    }

    async Task LoadClipAsync(string path)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        Pause();
        _mixer?.Dispose();
        _mixer = null;
        _mediaOpened = false;
        _info = null;
        _clipPath = path;
        _duration = _position = _in = _out = TimeSpan.Zero;
        SpeedList.SelectedIndex = NormalSpeedIndex;
        _speed = 1.0;
        BrightnessSlider.Value = 0;
        ContrastSlider.Value = 100;
        SaturationSlider.Value = 100;
        FadeInSlider.Value = 0;
        FadeOutSlider.Value = 0;
        MirrorSwitch.IsChecked = false;
        _rotation = 0;
        TextBox.Text = "";
        TextPosBox.SelectedIndex = 0;
        TextSizeSlider.Value = 100;
        _cuts.Clear();
        TransitionBox.SelectedIndex = 0;
        TransitionSlider.Value = 500;
        RebuildCuts();
        UpdateTransitionUi();
        ApplySpeed();
        UpdateEffectsUi();
        DetachJob();
        ResetExportUi();

        var title = Path.GetFileNameWithoutExtension(path);
        ClipTitle.Text = title;
        ClipMeta.Text = "Читаем файл…";
        FileNameBox.Text = $"{title} (обрезка)";
        MediaError.Visibility = Visibility.Collapsed;
        PausedBadge.Visibility = Visibility.Collapsed;
        Timeline.Filmstrip = null;
        Timeline.Waveform = null;
        Timeline.Duration = TimeSpan.Zero;
        Timeline.Position = TimeSpan.Zero;
        ShowEmpty(false);
        UpdateTimeLabel();
        UpdateRangeUi();

        try
        {
            var info = await ClipMediaInfo.ProbeAsync(path, cts.Token);
            if (cts.IsCancellationRequested) return;

            _info = info;
            _duration = info.Duration;
            _in = TimeSpan.Zero;
            _out = _duration;
            Timeline.HasAudio = info.HasAudio;
            Timeline.Duration = _duration;
            Timeline.InPoint = _in;
            Timeline.OutPoint = _out;
            Timeline.Position = TimeSpan.Zero;

            var parts = new List<string>();
            if (info.HasVideo) parts.Add($"{info.Width}×{info.Height}");
            if (info.Fps > 1) parts.Add($"{info.Fps:0.##} к/с");
            parts.Add(EditorTimeline.FormatTime(info.Duration));
            parts.Add(FormatSize(info.SizeBytes));
            if (info.HasSeparateTracks) parts.Add("3 дорожки");
            ClipMeta.Text = string.Join("  ·  ", parts);

            SetupAudioCard(info);
            UpdateRangeUi();
            UpdateTimeLabel();
            UpdatePresetHint();

            try
            {
                Media.IsMuted = true;
                Media.Close();
                Media.Source = null;
                Media.Source = new Uri(path);
                Media.Play();
            }
            catch (Exception ex) { ShowMediaError(ex.Message); }

            _timer.Start();
            if (ExportJob.Current is { IsDone: false } running && IsOurJob(running)) AttachJob(running);
            _ = AttachMixerAsync(path, info, cts.Token);
            await LoadTimelineAssetsAsync(path, info, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"Editor: cannot open {path}", ex);
            ClipMeta.Text = "Не удалось прочитать файл";
            ShowMediaError(ex.Message);
            try { AppServices.Notifier?.Show("Не удалось открыть клип", ex.Message, NotifyKind.Error); } catch { }
        }
    }

    async Task LoadTimelineAssetsAsync(string path, ClipMediaInfo info, CancellationToken ct)
    {
        var width = Timeline.TrackWidth > 50 ? Timeline.TrackWidth : 1000;
        var aspect = info.HasVideo ? (double)info.Width / info.Height : 16.0 / 9;
        var frames = (int)Math.Clamp(Math.Round(width / (54 * aspect)), 4, 80);
        _stripFrames = frames;
        _stripWidth = width;

        var waveTask = info.HasAudio ? TimelineAssets.GetWaveformAsync(path, 2000, 76, ct) : Task.FromResult<string?>(null);
        var stripTask = info.HasVideo ? TimelineAssets.GetFilmstripAsync(path, info.Duration, frames, 108, ct) : Task.FromResult<string?>(null);

        var wave = await waveTask;
        if (!ct.IsCancellationRequested && wave is not null) Timeline.Waveform = LoadBitmap(wave);
        var strip = await stripTask;
        if (!ct.IsCancellationRequested && strip is not null) Timeline.Filmstrip = LoadBitmap(strip);
    }

    async void OnResizeDebounce(object? sender, EventArgs e)
    {
        _resizeDebounce.Stop();
        if (_info is null || _clipPath is null || !_info.HasVideo || _loadCts is null) return;
        var width = Timeline.TrackWidth;
        if (width < 50 || _stripWidth <= 0 || Math.Abs(width - _stripWidth) / _stripWidth < 0.3) return;
        try
        {
            var aspect = (double)_info.Width / _info.Height;
            var frames = (int)Math.Clamp(Math.Round(width / (54 * aspect)), 4, 80);
            if (frames == _stripFrames) return;
            _stripFrames = frames;
            _stripWidth = width;
            var ct = _loadCts.Token;
            var strip = await TimelineAssets.GetFilmstripAsync(_clipPath, _info.Duration, frames, 108, ct);
            if (!ct.IsCancellationRequested && strip is not null) Timeline.Filmstrip = LoadBitmap(strip);
        }
        catch (Exception ex) { Log.Warn($"Filmstrip refresh failed: {ex.Message}"); }
    }

    static BitmapImage LoadBitmap(string file)
    {
        var b = new BitmapImage();
        b.BeginInit();
        b.CacheOption = BitmapCacheOption.OnLoad;
        b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        b.UriSource = new Uri(file);
        b.EndInit();
        b.Freeze();
        return b;
    }

    void ShowEmpty(bool empty)
    {
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EditorContent.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        _mediaOpened = true;
        Media.Pause();
        Media.Position = _position;
        Media.Volume = 1.0;
        try { Media.SpeedRatio = _speed; } catch (Exception ex) { Log.Warn($"SpeedRatio failed: {ex.Message}"); }
        Media.IsMuted = UseMixer || PreviewMute.IsChecked == true;
        MediaError.Visibility = Visibility.Collapsed;
        PausedBadge.Visibility = Visibility.Visible;
        if (_duration <= TimeSpan.Zero && Media.NaturalDuration.HasTimeSpan)
        {
            _duration = _out = Media.NaturalDuration.TimeSpan;
            Timeline.Duration = _duration;
            Timeline.OutPoint = _out;
            UpdateRangeUi();
            UpdateTimeLabel();
        }
    }

    void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _mediaOpened = false;
        var codec = _info?.VideoCodec ?? "";
        var hint = codec is "hevc" or "h265" ? "Для HEVC установи «Расширения для видео HEVC» из Microsoft Store. "
            : codec is "av1" ? "Для AV1 установи «AV1 Video Extension» из Microsoft Store. "
            : codec is "vp9" ? "Для VP9 установи «VP9 Video Extensions» из Microsoft Store. "
            : "Возможно, для этого формата нужны кодеки из Microsoft Store. ";
        ShowMediaError(hint + "Обрезка и экспорт всё равно работают — ориентируйся по ленте кадров.");
        Log.Warn($"MediaElement failed for {_clipPath}: {e.ErrorException?.Message}");
    }

    void ShowMediaError(string text)
    {
        MediaErrorText.Text = text;
        MediaError.Visibility = Visibility.Visible;
        PausedBadge.Visibility = Visibility.Collapsed;
    }

    void OnTick(object? sender, EventArgs e)
    {
        if (!_playing || !_mediaOpened) return;
        _position = Media.Position;
        var stopAt = _out < _duration - TimeSpan.FromMilliseconds(40) ? _out : _duration;
        if (_position >= stopAt && _position > _in)
        {
            Pause();
            _position = stopAt;
            Media.Position = stopAt;
        }
        else if (UseMixer && (_mixer!.Position - _position).Duration() > TimeSpan.FromMilliseconds(150))
        {
            _mixer.Seek(_position);
        }
        Timeline.Position = _position;
        UpdateTimeLabel();
    }

    async Task AttachMixerAsync(string path, ClipMediaInfo info, CancellationToken ct)
    {
        PreviewAudioMixer? mixer = null;
        try
        {
            mixer = await PreviewAudioMixer.CreateAsync(path, info, ct);
            if (mixer is null || ct.IsCancellationRequested || _clipPath != path) { mixer?.Dispose(); return; }
            _mixer = mixer;
            mixer.SetGains(SystemGain, MicGain, MasterGain);
            mixer.Muted = PreviewMute.IsChecked == true;
            if (SpeedChanged) { mixer.Pause(); return; }
            if (_mediaOpened) Media.IsMuted = true;
            if (_playing) mixer.Play(Media.Position);
        }
        catch (OperationCanceledException) { mixer?.Dispose(); }
        catch (Exception ex) { mixer?.Dispose(); Log.Warn("Editor preview mixer failed: " + ex.Message); }
    }

    void TogglePlay()
    {
        if (!_mediaOpened) return;
        if (_playing) { Pause(); return; }
        var end = _out < _duration - TimeSpan.FromMilliseconds(40) ? _out : _duration;
        if (_position >= end - TimeSpan.FromMilliseconds(30)) Seek(_in);
        try
        {
            Media.Volume = 1.0;
            Media.IsMuted = UseMixer || PreviewMute.IsChecked == true;
            Media.Play();
            if (UseMixer) { _mixer!.Muted = PreviewMute.IsChecked == true; _mixer.Play(_position); }
            _playing = true;
            PlayIcon.Symbol = Symbol.Pause24;
            PlayIcon.Margin = new Thickness(0);
            PausedBadge.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { Log.Warn($"Play failed: {ex.Message}"); }
    }

    void Pause()
    {
        if (_playing)
        {
            try { Media.Pause(); } catch { }
        }
        _mixer?.Pause();
        _playing = false;
        PlayIcon.Symbol = Symbol.Play24;
        PlayIcon.Margin = new Thickness(2, 0, 0, 0);
        if (_mediaOpened && MediaError.Visibility != Visibility.Visible) PausedBadge.Visibility = Visibility.Visible;
    }

    void Seek(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t > _duration) t = _duration;
        _position = t;
        if (_mediaOpened)
        {
            try { Media.Position = t; } catch { }
        }
        if (UseMixer) _mixer!.Seek(t);
        Timeline.Position = t;
        UpdateTimeLabel();
    }

    void Step(TimeSpan delta)
    {
        Pause();
        Seek(_position + delta);
    }

    void OnScrubbing(object? sender, bool active)
    {
        if (active)
        {
            _scrubWasPlaying = _playing;
            Pause();
            Focus();
        }
        else if (_scrubWasPlaying)
        {
            _scrubWasPlaying = false;
            TogglePlay();
        }
    }

    void UpdateTimeLabel() =>
        TimeLabel.Text = $"{EditorTimeline.FormatTime(_position)} / {EditorTimeline.FormatTime(_duration)}";

    void SetIn()
    {
        var max = _out - MinRange;
        _in = _position > max ? max : _position;
        if (_in < TimeSpan.Zero) _in = TimeSpan.Zero;
        Timeline.InPoint = _in;
        UpdateRangeUi();
    }

    void SetOut()
    {
        var min = _in + MinRange;
        _out = _position < min ? min : _position;
        if (_out > _duration) _out = _duration;
        Timeline.OutPoint = _out;
        UpdateRangeUi();
    }

    void ResetRange()
    {
        _in = TimeSpan.Zero;
        _out = _duration;
        Timeline.InPoint = _in;
        Timeline.OutPoint = _out;
        UpdateRangeUi();
    }

    void UpdateRangeUi()
    {
        InLabel.Text = EditorTimeline.FormatTime(_in);
        OutLabel.Text = EditorTimeline.FormatTime(_out);
        SelLabel.Text = EditorTimeline.FormatTime(_out - _in);
        if (SpeedChanged)
        {
            var len = TimeSpan.FromSeconds((_out - _in).TotalSeconds / _speed);
            SpeedHint.Text = $"Длительность на выходе: {EditorTimeline.FormatTime(len)}. " +
                             "Требуется перекодирование — быстрое копирование недоступно.";
        }
        UpdatePresetHint();
    }

    void SetupAudioCard(ClipMediaInfo info)
    {
        AudioCard.Visibility = info.HasAudio ? Visibility.Visible : Visibility.Collapsed;
        SeparatePanel.Visibility = info.HasSeparateTracks ? Visibility.Visible : Visibility.Collapsed;
        SinglePanel.Visibility = info.HasAudio && !info.HasSeparateTracks ? Visibility.Visible : Visibility.Collapsed;

        _updatingAudioUi = true;
        SysSlider.Value = MicSlider.Value = MasterSlider.Value = 100;
        SysMute.IsChecked = MicMute.IsChecked = MasterMute.IsChecked = false;
        _updatingAudioUi = false;
        UpdateAudioUi();
    }

    float SystemGain => SysMute.IsChecked == true ? 0f : (float)(SysSlider.Value / 100);
    float MicGain => MicMute.IsChecked == true ? 0f : (float)(MicSlider.Value / 100);
    float MasterGain => MasterMute.IsChecked == true ? 0f : (float)(MasterSlider.Value / 100);

    bool AudioChanged => _info is { HasAudio: true } info && (info.HasSeparateTracks
        ? Math.Abs(SystemGain - 1) > 0.005 || Math.Abs(MicGain - 1) > 0.005
        : Math.Abs(MasterGain - 1) > 0.005);

    void UpdateAudioUi()
    {
        if (_updatingAudioUi) return;
        SysPercent.Text = $"{(int)Math.Round(SysSlider.Value)} %";
        MicPercent.Text = $"{(int)Math.Round(MicSlider.Value)} %";
        MasterPercent.Text = $"{(int)Math.Round(MasterSlider.Value)} %";
        SysSlider.IsEnabled = SysMute.IsChecked != true;
        MicSlider.IsEnabled = MicMute.IsChecked != true;
        MasterSlider.IsEnabled = MasterMute.IsChecked != true;
        SysMuteIcon.Symbol = SysMute.IsChecked == true ? Symbol.SpeakerMute20 : Symbol.Speaker220;
        MicMuteIcon.Symbol = MicMute.IsChecked == true ? Symbol.MicOff20 : Symbol.Mic20;
        MasterMuteIcon.Symbol = MasterMute.IsChecked == true ? Symbol.SpeakerMute20 : Symbol.Speaker220;
        AudioChangedHint.Visibility = AudioChanged ? Visibility.Visible : Visibility.Collapsed;
        _mixer?.SetGains(SystemGain, MicGain, MasterGain);
        UpdatePresetHint();
    }

    void OnAudioSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateAudioUi();
    void OnAudioMuteChanged(object sender, RoutedEventArgs e) => UpdateAudioUi();

    void OnPreviewMuteChanged(object sender, RoutedEventArgs e)
    {
        var muted = PreviewMute.IsChecked == true;
        PreviewMuteIcon.Symbol = muted ? Symbol.SpeakerMute20 : Symbol.Speaker220;
        if (UseMixer) _mixer!.Muted = muted;
        else if (_mediaOpened) Media.IsMuted = muted;
    }

    bool SpeedChanged => Math.Abs(_speed - 1.0) > 0.001;

    bool UseMixer => _mixer is not null && !SpeedChanged;

    double SelectedSpeed => (SpeedList.SelectedItem as SpeedItem)?.Value ?? 1.0;

    void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        _speed = SelectedSpeed;
        ApplySpeed();
    }

    double EffBrightness => BrightnessSlider.Value / 200.0;
    double EffContrast => ContrastSlider.Value / 100.0;
    double EffSaturation => SaturationSlider.Value / 100.0;
    TimeSpan EffFadeIn => TimeSpan.FromMilliseconds(FadeInSlider.Value);
    TimeSpan EffFadeOut => TimeSpan.FromMilliseconds(FadeOutSlider.Value);
    bool EffMirror => MirrorSwitch.IsChecked == true;

    int _rotation;
    void OnRotateRightClick(object sender, RoutedEventArgs e) { _rotation = (_rotation + 90) % 360; UpdateEffectsUi(); Focus(); }
    void OnRotateLeftClick(object sender, RoutedEventArgs e) { _rotation = (_rotation + 270) % 360; UpdateEffectsUi(); Focus(); }

    void OnAddCutClick(object sender, RoutedEventArgs e)
    {
        if (_info is null || _clipPath is null) return;
        if (_out - _in < MinRange)
        {
            AppServices.Notifier?.Show("Слишком короткий фрагмент", "Выбери хотя бы 0,1 с", NotifyKind.Warning);
            return;
        }
        _cuts.Add(new MediaCut(_clipPath, Path.GetFileNameWithoutExtension(_clipPath), _in, _out, _info.HasAudio));
        RebuildCuts();
        Focus();
    }

    async void OnAddFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Добавить видео в склейку",
                Filter = "Видео|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.ts;*.m4v|Все файлы|*.*",
            };
            var folder = AppServices.Settings?.Current.ClipsFolder;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) dlg.InitialDirectory = folder;
            if (dlg.ShowDialog() != true) return;

            var clipAtStart = _clipPath;
            var info = await ClipMediaInfo.ProbeAsync(dlg.FileName);

            if (!ReferenceEquals(clipAtStart, _clipPath) && !string.Equals(clipAtStart, _clipPath, StringComparison.Ordinal))
                return;
            if (!info.HasVideo)
            {
                AppServices.Notifier?.Show("Не видеофайл", "В файле нет видеодорожки", NotifyKind.Warning);
                return;
            }
            _cuts.Add(new MediaCut(dlg.FileName, Path.GetFileNameWithoutExtension(dlg.FileName), TimeSpan.Zero, info.Duration, info.HasAudio));
            RebuildCuts();
        }
        catch (Exception ex)
        {
            Log.Error("Add file to timeline failed", ex);
            AppServices.Notifier?.Show("Не удалось добавить файл", ex.Message, NotifyKind.Error);
        }
    }

    void OnRemoveCutClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int i } && i >= 0 && i < _cuts.Count)
        {
            _cuts.RemoveAt(i);
            RebuildCuts();
        }
    }

    void RebuildCuts()
    {
        if (CutsPanel is null) return;
        CutsPanel.Children.Clear();
        for (var i = 0; i < _cuts.Count; i++)
        {
            var cut = _cuts[i];
            var a = cut.In; var b = cut.Out;
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var external = _clipPath is null || !string.Equals(cut.Path, _clipPath, StringComparison.OrdinalIgnoreCase);
            var prefix = external ? $"📎 {cut.Name}  " : "";
            var tb = new TextBlock
            {
                Text = $"{i + 1}.  {prefix}{EditorTimeline.FormatTime(a)} – {EditorTimeline.FormatTime(b)}  ({EditorTimeline.FormatTime(b - a)})",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TryFindResource("CB.TextSecondary") as Brush ?? Brushes.Gray,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var btn = new Button
            {
                Style = TryFindResource("CB.IconButton") as Style,
                Width = 26, Height = 26, Tag = i, ToolTip = "Убрать кусок",
                Content = new Wpf.Ui.Controls.SymbolIcon { Symbol = Symbol.Dismiss20, FontSize = 15 },
            };
            btn.Click += OnRemoveCutClick;
            Grid.SetColumn(btn, 1);
            grid.Children.Add(tb);
            grid.Children.Add(btn);
            CutsPanel.Children.Add(new Border
            {
                Background = TryFindResource("CB.Control") as Brush ?? Brushes.DimGray,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 4),
                Child = grid,
            });
        }
        CutsCount.Text = _cuts.Count == 0 ? "0" : $"{_cuts.Count} · {EditorTimeline.FormatTime(TotalCutsDuration())}";
        CutsHint.Text = _cuts.Count >= 2
            ? "При экспорте фрагменты склеятся по порядку. Эффекты применяются ко всей склейке."
            : "Добавь два и более фрагмента — они склеятся в один клип по порядку.";
        TransitionRow.Visibility = _cuts.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    TimeSpan TotalCutsDuration()
    {
        var t = TimeSpan.Zero;
        foreach (var c in _cuts) t += c.Out - c.In;
        return t;
    }

    bool AllCutsFromMainClip => _clipPath is not null &&
        _cuts.TrueForAll(c => string.Equals(c.Path, _clipPath, StringComparison.OrdinalIgnoreCase));

    List<(TimeSpan, TimeSpan)>? BuildSameFileSegments()
    {
        if (_cuts.Count < 2 || !AllCutsFromMainClip) return null;
        var list = new List<(TimeSpan, TimeSpan)>(_cuts.Count);
        foreach (var c in _cuts) list.Add((c.In, c.Out));
        return list;
    }

    List<MediaSegment>? BuildMediaSegments()
    {
        if (_cuts.Count < 2 || AllCutsFromMainClip) return null;
        var list = new List<MediaSegment>(_cuts.Count);
        foreach (var c in _cuts) list.Add(new MediaSegment(c.Path, c.In, c.Out, c.HasAudio));
        return list;
    }

    void OnTransitionChanged(object sender, SelectionChangedEventArgs e) => UpdateTransitionUi();
    void OnTransitionSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateTransitionUi();

    string? TransitionType => (TransitionBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
    TimeSpan TransitionDuration => TransitionType is null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(TransitionSlider.Value);

    void UpdateTransitionUi()
    {
        if (TransitionValue is null) return;
        TransitionValue.Text = $"{TransitionSlider.Value / 1000.0:0.0} с";
        var on = TransitionType is not null;
        TransitionValue.Visibility = TransitionSlider.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    string? EffText => string.IsNullOrWhiteSpace(TextBox.Text) ? null : TextBox.Text.Trim();
    TextPosition EffTextPosition => TextPosBox.SelectedIndex switch { 1 => TextPosition.Center, 2 => TextPosition.Top, _ => TextPosition.Bottom };
    double EffTextScale => TextSizeSlider.Value / 100.0;

    bool EffectsChanged =>
        Math.Abs(EffBrightness) > 0.001 || Math.Abs(EffContrast - 1) > 0.001 || Math.Abs(EffSaturation - 1) > 0.001
        || EffMirror || _rotation != 0 || EffFadeIn > TimeSpan.Zero || EffFadeOut > TimeSpan.Zero || EffText is not null;

    void OnEffectSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateEffectsUi();
    void OnMirrorChanged(object sender, RoutedEventArgs e) => UpdateEffectsUi();
    void OnTextEffectChanged(object sender, TextChangedEventArgs e) => UpdateEffectsUi();
    void OnTextPosChanged(object sender, SelectionChangedEventArgs e) => UpdateEffectsUi();

    void OnEffectsResetClick(object sender, RoutedEventArgs e)
    {
        BrightnessSlider.Value = 0;
        ContrastSlider.Value = 100;
        SaturationSlider.Value = 100;
        FadeInSlider.Value = 0;
        FadeOutSlider.Value = 0;
        MirrorSwitch.IsChecked = false;
        _rotation = 0;
        TextBox.Text = "";
        TextPosBox.SelectedIndex = 0;
        TextSizeSlider.Value = 100;
        UpdateEffectsUi();
        Focus();
    }

    void UpdateEffectsUi()
    {

        if (EffectsHint is null) return;
        RotationValue.Text = $"{_rotation}°";
        BrightnessValue.Text = $"{(int)Math.Round(BrightnessSlider.Value)}";
        ContrastValue.Text = $"{(int)Math.Round(ContrastSlider.Value)} %";
        SaturationValue.Text = $"{(int)Math.Round(SaturationSlider.Value)} %";
        FadeInValue.Text = FadeInSlider.Value < 1 ? "Выкл" : $"{FadeInSlider.Value / 1000.0:0.0} с";
        FadeOutValue.Text = FadeOutSlider.Value < 1 ? "Выкл" : $"{FadeOutSlider.Value / 1000.0:0.0} с";
        EffectsHint.Visibility = EffectsChanged ? Visibility.Visible : Visibility.Collapsed;
        UpdatePresetHint();
    }

    void ApplySpeed()
    {
        SpeedValue.Text = Speeds.FirstOrDefault(s => Math.Abs(s.Value - _speed) < 0.001)?.Label ?? "1×";
        SpeedHint.Visibility = SpeedChanged ? Visibility.Visible : Visibility.Collapsed;
        if (SpeedChanged)
        {
            var len = TimeSpan.FromSeconds((_out - _in).TotalSeconds / _speed);
            SpeedHint.Text = $"Длительность на выходе: {EditorTimeline.FormatTime(len)}. " +
                             "Требуется перекодирование — быстрое копирование недоступно.";
        }

        if (_mediaOpened)
        {
            try { Media.SpeedRatio = _speed; } catch (Exception ex) { Log.Warn($"SpeedRatio failed: {ex.Message}"); }
        }

        if (SpeedChanged)
        {
            _mixer?.Pause();
            if (_mediaOpened) Media.IsMuted = PreviewMute.IsChecked == true;
        }
        else if (_mixer is { } mixer)
        {
            if (_mediaOpened) Media.IsMuted = true;
            mixer.Muted = PreviewMute.IsChecked == true;
            if (_playing) mixer.Play(_position);
        }

        UpdatePresetHint();
    }

    PresetItem SelectedPreset => PresetList.SelectedItem as PresetItem ?? Presets[0];

    void OnPresetChanged(object sender, SelectionChangedEventArgs e) => UpdatePresetHint();

    void UpdatePresetHint()
    {
        if (_info is null) { PresetHint.Text = ""; return; }
        var len = _out - _in;
        var kind = SelectedPreset.Preset.Kind;
        PresetHint.Text = kind switch
        {
            ExportPresetKind.FastCopy when _cuts.Count >= 2 => "Склейка кусков требует перекодирования точно по кадрам.",
            ExportPresetKind.FastCopy when SpeedChanged || EffectsChanged => "Из-за скорости или эффектов клип будет перекодирован точно по кадрам.",
            ExportPresetKind.FastCopy when AudioChanged => "Видео копируется без потерь, звук пересобирается с новой громкостью.",
            ExportPresetKind.FastCopy => "Все дорожки сохраняются как есть. Границы сдвинутся к ближайшему ключевому кадру.",
            ExportPresetKind.Precise => "Точная обрезка по кадрам, одна дорожка — микс.",
            ExportPresetKind.Discord => len.TotalSeconds > 90
                ? $"{Actions.FormatDuration(len)} в 10 МБ — качество будет низким. Лучше укоротить фрагмент."
                : $"≈ {DiscordKbps(len) / 1000.0:0.0} Мбит/с видео, {(_info.Height > 720 ? "720p" : $"{_info.Height}p")}.",
            ExportPresetKind.Vertical => "Центральная часть кадра, 1080×1920.",
            ExportPresetKind.Gif => len.TotalSeconds > 15 ? "GIF длиннее 15 с получится очень большим." : "Без звука, 15 к/с, ширина 480 px.",
            ExportPresetKind.Mp3 => _info.HasAudio ? "Звуковая дорожка (микс) в MP3." : "В этом клипе нет звука.",
            _ => "",
        };
    }

    static int DiscordKbps(TimeSpan len)
    {
        var seconds = Math.Max(0.1, len.TotalSeconds);
        return Math.Clamp((int)(ExportService.DiscordLimitBytes * 8.0 / 1000 * 0.94 / seconds - 128 - 8), 150, 12000);
    }

    void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_info is null || _clipPath is null) return;
            if (_job is { IsDone: false }) return;

            if (ExportJob.Current is { IsDone: false })
            {
                AppServices.Notifier?.Show("Экспорт уже идёт", "Дождись окончания текущего экспорта", NotifyKind.Warning);
                return;
            }
            if (_cuts.Count < 2 && _out - _in < MinRange)
            {
                AppServices.Notifier?.Show("Слишком короткий фрагмент", "Выбери хотя бы 0,1 с", NotifyKind.Warning);
                return;
            }
            if (_cuts.Count == 1 && !AllCutsFromMainClip)
            {
                AppServices.Notifier?.Show("Нужен ещё фрагмент",
                    "Один добавленный файл нельзя склеить. Добавь второй фрагмент, либо открой файл через «Открыть файл».", NotifyKind.Warning);
                return;
            }
            Pause();
            var request = new ExportRequest
            {
                SourcePath = _clipPath,
                Info = _info,
                Preset = SelectedPreset.Preset.Kind,
                In = _in,
                Out = _out,
                Title = string.IsNullOrWhiteSpace(FileNameBox.Text) ? $"{Path.GetFileNameWithoutExtension(_clipPath)} (обрезка)" : FileNameBox.Text,
                SystemGain = SystemGain,
                MicGain = MicGain,
                MasterGain = MasterGain,
                Speed = _speed,
                Brightness = EffBrightness,
                Contrast = EffContrast,
                Saturation = EffSaturation,
                Mirror = EffMirror,
                Rotation = _rotation,
                FadeIn = EffFadeIn,
                FadeOut = EffFadeOut,
                Text = EffText,
                TextPosition = EffTextPosition,
                TextScale = EffTextScale,
                Segments = BuildSameFileSegments(),
                MediaSegments = BuildMediaSegments(),
                Transition = _cuts.Count >= 2 ? TransitionDuration : TimeSpan.Zero,
                TransitionType = TransitionType ?? "fade",
            };
            AttachJob(ExportJob.Start(request));
        }
        catch (Exception ex)
        {
            Log.Error("Export start failed", ex);
            try { AppServices.Notifier?.Show("Ошибка экспорта", ex.Message, NotifyKind.Error); } catch { }
        }
    }

    void AttachJob(ExportJob job)
    {
        DetachJob();
        _job = job;
        job.Changed += OnJobChanged;
        RenderJob(job);
    }

    void DetachJob()
    {
        if (_job is null) return;
        _job.Changed -= OnJobChanged;
        _job = null;
    }

    void OnJobChanged(ExportJob job) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => { if (ReferenceEquals(job, _job)) RenderJob(job); });

    void RenderJob(ExportJob job)
    {
        if (!job.IsDone)
        {
            ProgressPanel.Visibility = Visibility.Visible;
            ResultPanel.Visibility = ErrorPanel.Visibility = Visibility.Collapsed;
            ExportButton.IsEnabled = false;
            ExportProgress.Value = Math.Clamp(job.Fraction * 100, 0, 100);
            ExportProgress.IsIndeterminate = job.Fraction <= 0 && !job.IsCancelled;
            ProgressStatus.Text = job.Status;
            ProgressPercent.Text = $"{(int)(job.Fraction * 100)} %";
            return;
        }

        ProgressPanel.Visibility = Visibility.Collapsed;
        ExportButton.IsEnabled = true;
        if (job.Succeeded && job.OutputPath is { } path)
        {
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            ResultTitle.Text = size > 0 ? $"Готово  ·  {FormatSize(size)}" : "Готово";
            ResultFile.Text = Path.GetFileName(path);
            ResultPanel.Visibility = Visibility.Visible;
        }
        else if (!job.IsCancelled)
        {
            ErrorText.Text = job.Error ?? "Неизвестная ошибка";
            ErrorPanel.Visibility = Visibility.Visible;
        }
        job.Changed -= OnJobChanged;
        ExportJob.Clear();
    }

    void ResetExportUi()
    {
        ProgressPanel.Visibility = ResultPanel.Visibility = ErrorPanel.Visibility = Visibility.Collapsed;
        ExportButton.IsEnabled = true;
        ExportProgress.Value = 0;
    }

    void OnCancelExportClick(object sender, RoutedEventArgs e) => _job?.Cancel();

    void OnShowInFolderClick(object sender, RoutedEventArgs e)
    {
        var path = _job?.OutputPath;
        if (path is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Explorer failed: {ex.Message}"); }
    }

    void OnOpenResultClick(object sender, RoutedEventArgs e)
    {
        var path = _job?.OutputPath;
        if (path is null || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log.Warn($"Open failed: {ex.Message}");
            try { AppServices.Notifier?.Show("Не удалось открыть файл", ex.Message, NotifyKind.Warning); } catch { }
        }
    }

    void OnShowClipInFolderClick(object sender, RoutedEventArgs e)
    {
        var path = _clipPath;
        if (path is null) return;
        try
        {
            Pause();
            AppServices.Shell?.HideOverlay();
            var clip = AppServices.Library?.Items.FirstOrDefault(c => string.Equals(c.FilePath, path, StringComparison.OrdinalIgnoreCase))
                       ?? new ClipInfo { FilePath = path };
            AppServices.Library?.ShowInExplorer(clip);
        }
        catch (Exception ex) { Log.Error("Editor: show in folder failed", ex); }
    }

    public event Action? ClipDeleted;

    async void OnDeleteClipClick(object sender, RoutedEventArgs e)
    {
        try { await DeleteCurrentClipAsync(); }
        catch (Exception ex)
        {
            Log.Error("Editor: delete failed", ex);
            AppServices.Notifier?.Show("Не удалось удалить клип", ex.Message, NotifyKind.Error);
        }
    }

    async Task DeleteCurrentClipAsync()
    {
        var path = _clipPath;
        if (path is null || AppServices.Library is not { } lib) return;
        if (ExportJob.Current is { IsDone: false } job && string.Equals(job.Request.SourcePath, path, StringComparison.OrdinalIgnoreCase))
        {
            AppServices.Notifier?.Show("Сейчас идёт экспорт этого клипа", "Дождись окончания или отмени экспорт", NotifyKind.Warning);
            return;
        }

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Удалить клип?",
            Content = new TextBlock
            {
                Text = $"«{Path.GetFileNameWithoutExtension(path)}» будет перемещён в корзину.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
            },
            PrimaryButtonText = "Удалить",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
            CloseButtonText = "Отмена",
        };
        try { if (Window.GetWindow(this) is { } owner) { box.Owner = owner; box.Topmost = owner.Topmost; } } catch { }
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        _loadCts?.Cancel();
        Pause();
        _timer.Stop();
        try { Media.Stop(); Media.Close(); Media.Source = null; } catch { }
        _mixer?.Dispose();
        _mixer = null;
        _mediaOpened = false;

        var clip = lib.Items.FirstOrDefault(c => string.Equals(c.FilePath, path, StringComparison.OrdinalIgnoreCase))
                   ?? new ClipInfo { FilePath = path };
        for (var attempt = 1; ; attempt++)
        {
            try { await lib.DeleteAsync(clip); break; }
            catch (IOException) when (attempt < 6) { await Task.Delay(250); }
        }

        _clipPath = null;
        _info = null;
        ShowEmpty(true);
        AppServices.Notifier?.Show("Клип удалён", "Файл перемещён в корзину", NotifyKind.Info);
        ClipDeleted?.Invoke();
    }

    void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Открыть клип",
                Filter = "Видео и звук|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.ts;*.m4v;*.mp3;*.wav;*.m4a|Все файлы|*.*",
            };
            var folder = AppServices.Settings?.Current.ClipsFolder;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) dlg.InitialDirectory = folder;
            if (dlg.ShowDialog() == true) LoadClip(dlg.FileName);
        }
        catch (Exception ex)
        {
            Log.Error("OpenFileDialog failed", ex);
        }
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0]))
                LoadClip(files[0]);
        }
        catch (Exception ex) { Log.Warn($"Drop failed: {ex.Message}"); }
        e.Handled = true;
    }

    void OnPreviewClick(object sender, MouseButtonEventArgs e)
    {
        Focus();
        TogglePlay();
    }

    void OnPlayClick(object sender, RoutedEventArgs e) { TogglePlay(); Focus(); }
    void OnStepBackClick(object sender, RoutedEventArgs e) { Step(-(_info?.FrameDuration ?? TimeSpan.FromSeconds(1 / 30.0))); Focus(); }
    void OnStepForwardClick(object sender, RoutedEventArgs e) { Step(_info?.FrameDuration ?? TimeSpan.FromSeconds(1 / 30.0)); Focus(); }
    void OnSetInClick(object sender, RoutedEventArgs e) { SetIn(); Focus(); }
    void OnSetOutClick(object sender, RoutedEventArgs e) { SetOut(); Focus(); }
    void OnResetRangeClick(object sender, RoutedEventArgs e) { ResetRange(); Focus(); }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_info is null) return;
        var focused = Keyboard.FocusedElement;
        if (focused is TextBoxBase) return;
        if (focused is System.Windows.Controls.ComboBox) return;
        if (focused is ButtonBase && e.Key is Key.Space or Key.Enter) return;
        if (focused is Slider && e.Key is Key.Left or Key.Right or Key.Home or Key.End) return;

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var frame = _info.FrameDuration;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Delete: OnDeleteClipClick(this, new RoutedEventArgs()); break;
            case Key.Left: Step(shift ? TimeSpan.FromSeconds(-1) : -frame); break;
            case Key.Right: Step(shift ? TimeSpan.FromSeconds(1) : frame); break;
            case Key.I: SetIn(); break;
            case Key.O: SetOut(); break;
            case Key.Home: Pause(); Seek(_in); break;
            case Key.End: Pause(); Seek(_out); break;
            default: return;
        }
        e.Handled = true;
    }

    static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} МБ",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} ГБ",
    };
}

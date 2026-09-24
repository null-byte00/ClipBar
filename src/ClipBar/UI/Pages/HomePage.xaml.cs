using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipBar.Core;
using Wpf.Ui.Controls;

namespace ClipBar.UI.Pages;

public partial class HomePage : Page
{
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _meters = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly ObservableCollection<ClipInfo> _recent = [];
    IReadOnlyList<EncoderInfo>? _encoders;
    string? _visualState;
    double _sysShown, _micShown;
    bool _syncingToggle, _sysHot, _micHot;

    public HomePage()
    {
        InitializeComponent();
        ClipList.ItemsSource = _recent;
        _clock.Tick += (_, _) => RefreshStatus();
        _meters.Tick += (_, _) => TickMeters();
        IsVisibleChanged += OnVisibleChanged;
        Loaded += (_, _) => { RefreshAll(); };

        if (AppServices.Engine is { } engine) engine.StateChanged += (_, _) => Dispatcher.BeginInvoke(RefreshAll);
        if (AppServices.Audio is { } audio) audio.Changed += (_, _) => Dispatcher.BeginInvoke(RefreshChips);
        if (AppServices.Settings is { } settings) settings.Changed += (_, _) => Dispatcher.BeginInvoke(RefreshAll);
        if (AppServices.Library is { } library) library.Items.CollectionChanged += OnLibraryChanged;
        _ = LoadEncodersAsync();
        RefreshHotkeys();
        RefreshRecent();
    }

    void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var visible = (bool)e.NewValue;
        try
        {
            if (visible)
            {
                AppServices.Audio?.SetMonitoring(true);
                _clock.Start();
                _meters.Start();
                RefreshAll();
            }
            else
            {
                _clock.Stop();
                _meters.Stop();
                AppServices.Audio?.SetMonitoring(false);
            }
        }
        catch (Exception ex) { Log.Error("HomePage visibility switch failed", ex); }
    }

    async Task LoadEncodersAsync()
    {
        try
        {
            if (AppServices.Engine is not { } engine) return;
            _encoders = await engine.GetAvailableEncodersAsync();
            await Dispatcher.BeginInvoke(RefreshChips);
        }
        catch (Exception ex) { Log.Warn($"Encoder list unavailable: {ex.Message}"); }
    }

    void RefreshAll()
    {
        RefreshStatus();
        RefreshChips();
        RefreshHotkeys();
    }

    void RefreshStatus()
    {
        try
        {
            var engine = AppServices.Engine;
            var settings = AppServices.Settings?.Current;
            var state = engine?.State ?? EngineState.Stopped;
            var recording = engine?.IsRecording == true;
            var replay = engine?.ReplayActive == true;
            var keep = Actions.FormatDuration(TimeSpan.FromSeconds(settings?.ReplaySeconds ?? 300));

            string title, subtitle, brushKey;
            if (recording)
            {
                title = "Идёт запись";
                subtitle = replay
                    ? $"Откат тоже работает · хранятся последние {keep}"
                    : "Откат выключен — пишется только эта запись";
                brushKey = "CB.Record";
            }
            else switch (state)
            {
                case EngineState.Buffering when replay:
                    title = "Откат работает";
                    subtitle = $"Хранятся последние {keep} · в буфере {Fmt(engine!.BufferedDuration)}";
                    brushKey = "CB.Success";
                    break;
                case EngineState.Starting:
                    title = "Запускаем захват…";
                    subtitle = "Это займёт пару секунд";
                    brushKey = "CB.Warning";
                    break;
                case EngineState.Error:
                    title = "Захват не работает";
                    subtitle = "Проверь настройки видео и нажми «Перезапустить»";
                    brushKey = "CB.Record";
                    break;
                default:
                    title = "Откат выключен";
                    subtitle = $"Включи откат, чтобы всегда иметь под рукой последние {keep}";
                    brushKey = "CB.TextTertiary";
                    break;
            }

            StatusTitle.Text = title;
            StatusSubtitle.Text = subtitle;
            ApplyVisualState(brushKey, recording || state == EngineState.Starting);

            ErrorBar.Message = engine?.LastError ?? "";
            ErrorBar.IsOpen = state == EngineState.Error;

            _syncingToggle = true;
            ReplayToggle.IsChecked = replay;
            ReplayToggle.IsEnabled = engine is not null;
            _syncingToggle = false;

            SaveButton.IsEnabled = replay && state == EngineState.Buffering;
            SaveHotkey.Text = Actions.HotkeyText(HotkeyAction.SaveReplay);
            RecordButton.IsEnabled = engine is not null;
            RecordLabel.Text = recording ? "Остановить запись" : "Начать запись";
            RecordIcon.Symbol = recording ? SymbolRegular.RecordStop24 : SymbolRegular.Record24;
            RecordHotkey.Text = recording
                ? $"{Fmt(engine!.RecordingElapsed)} · {Actions.HotkeyText(HotkeyAction.ToggleRecording)}"
                : Actions.HotkeyText(HotkeyAction.ToggleRecording);
            ShotHotkey.Text = Actions.HotkeyText(HotkeyAction.Screenshot);
            ClipsEmptyHint.Text = $"Сохрани первый откат: {Actions.HotkeyText(HotkeyAction.SaveReplay)}";
        }
        catch (Exception ex) { Log.Error("HomePage status refresh failed", ex); }
    }

    void ApplyVisualState(string brushKey, bool pulse)
    {
        var key = brushKey + (pulse ? "+" : "");
        if (key == _visualState) return;
        _visualState = key;

        var brush = (SolidColorBrush)FindResource(brushKey);
        StatusDot.Fill = brush;
        StatusHalo.Fill = brush;
        var c = brush.Color;
        GlowStop.Color = Color.FromArgb(0x30, c.R, c.G, c.B);
        GlowStopEnd.Color = Color.FromArgb(0x00, c.R, c.G, c.B);

        if (pulse)
        {
            var anim = new DoubleAnimation(0.7, 1.35, TimeSpan.FromMilliseconds(1000))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
        else
        {
            HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            HaloScale.ScaleX = HaloScale.ScaleY = 1;
        }
    }

    void RefreshChips()
    {
        try
        {
            var engine = AppServices.Engine;
            var settings = AppServices.Settings?.Current;
            var audio = AppServices.Audio;

            var encoderId = engine?.ActiveEncoder;
            if (encoderId is not null)
                EncoderChip.Text = _encoders?.FirstOrDefault(e => e.Id == encoderId)?.DisplayName ?? PrettyEncoder(encoderId);
            else
                EncoderChip.Text = settings is { Encoder: not "auto" and { Length: > 0 } }
                    ? PrettyEncoder(settings.Encoder)
                    : "Авто-выбор энкодера";

            var (w, h) = OutputSize(settings);
            VideoChip.Text = $"{w}×{h} · {settings?.Fps ?? 60} fps";

            var sys = settings?.RecordSystemAudio ?? true;
            var mic = settings?.RecordMic ?? true;
            var muted = audio?.MicMuted ?? settings?.MicMuted ?? false;
            AudioChip.Text = (sys, mic, muted) switch
            {
                (true, true, false) => "Система + микрофон",
                (true, true, true) => "Система · микрофон выключен",
                (true, false, _) => "Только звук системы",
                (false, true, false) => "Только микрофон",
                (false, true, true) => "Микрофон выключен",
                _ => "Без звука",
            };
            AudioChipIcon.Symbol = mic && muted ? SymbolRegular.MicOff20 : SymbolRegular.Speaker220;

            MicButtonIcon.Symbol = muted ? SymbolRegular.MicOff20 : SymbolRegular.Mic20;
            MicButtonIcon.Foreground = (Brush)FindResource(muted ? "CB.Record" : "CB.TextSecondary");
            MicLabel.Text = muted ? "Микрофон · выключен" : "Микрофон";
            MicBar.Opacity = muted ? 0.35 : 1;
            AudioHint.Text = settings is { SeparateAudioTracks: false }
                ? "Одна дорожка: система и микрофон вместе."
                : "Дорожки пишутся отдельно: микс, система, микрофон.";
        }
        catch (Exception ex) { Log.Error("HomePage chips refresh failed", ex); }
    }

    void RefreshHotkeys()
    {
        try
        {
            HotkeyList.Children.Clear();
            var hotkeys = AppServices.Settings?.Current.Hotkeys ?? AppSettings.DefaultHotkeys();
            foreach (var action in Enum.GetValues<HotkeyAction>())
            {
                hotkeys.TryGetValue(action, out var combo);
                HotkeyList.Children.Add(HotkeyRow(action.Title(), combo));
            }
        }
        catch (Exception ex) { Log.Error("HomePage hotkeys refresh failed", ex); }
    }

    UIElement HotkeyRow(string title, string? combo)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 124 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        if (string.IsNullOrEmpty(combo))
        {
            keys.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "не назначено", Style = (Style)FindResource("CB.Text.Caption"),
                Foreground = (Brush)FindResource("CB.TextTertiary"), FontStyle = FontStyles.Italic,
            });
        }
        else
        {
            var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                    keys.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text = "+", Margin = new Thickness(1, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Brush)FindResource("CB.TextTertiary"), FontSize = 11,
                    });
                keys.Children.Add(new Border
                {
                    Style = (Style)FindResource("Home.KeyCap"),
                    Child = new System.Windows.Controls.TextBlock
                    {
                        Text = parts[i], FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)FindResource("CB.TextPrimary"), HorizontalAlignment = HorizontalAlignment.Center,
                    },
                });
            }
        }
        row.Children.Add(keys);

        var label = new System.Windows.Controls.TextBlock
        {
            Text = title, Style = (Style)FindResource("CB.Text.Caption"), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    void OnLibraryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess()) RefreshRecent();
        else Dispatcher.BeginInvoke(RefreshRecent);
    }

    void RefreshRecent()
    {
        try
        {
            var items = AppServices.Library?.Items.Take(4).ToList() ?? [];
            if (items.SequenceEqual(_recent)) return;
            _recent.Clear();
            foreach (var c in items) _recent.Add(c);
            ClipsEmpty.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { Log.Error("HomePage recent clips refresh failed", ex); }
    }

    void TickMeters()
    {
        var audio = AppServices.Audio;
        if (audio is null) return;
        _sysShown = Math.Max(Curve(audio.SystemLevel), _sysShown * 0.86);
        _micShown = Math.Max(Curve(audio.MicLevel), _micShown * 0.86);
        SysBar.Width = SysTrack.ActualWidth * _sysShown;
        MicBar.Width = MicTrack.ActualWidth * _micShown;
        SysValue.Text = Db(audio.SystemLevel);
        MicValue.Text = audio.MicMuted ? "выкл" : Db(audio.MicLevel);
        Hot(SysBar, audio.SystemLevel > 0.95, ref _sysHot);
        Hot(MicBar, audio.MicLevel > 0.95, ref _micHot);

        static double Curve(float level) => Math.Sqrt(Math.Clamp(level, 0, 1));
        static string Db(float level) => level <= 0.0005 ? "" : $"{20 * Math.Log10(level):0} дБ";
    }

    void Hot(Border bar, bool hot, ref bool current)
    {
        if (hot == current) return;
        current = hot;
        bar.Background = (Brush)FindResource(hot ? "CB.Record" : "CB.Accent");
    }

    void OnSaveClick(object sender, RoutedEventArgs e) => _ = Actions.SaveReplayAsync();
    void OnRecordClick(object sender, RoutedEventArgs e) => _ = Actions.ToggleRecordingAsync();
    void OnScreenshotClick(object sender, RoutedEventArgs e) => _ = Actions.ScreenshotAsync();
    void OnMicClick(object sender, RoutedEventArgs e) => _ = Task.Run(Actions.ToggleMic);
    void OnEditHotkeysClick(object sender, RoutedEventArgs e) => AppServices.Shell?.ShowMain("settings");
    void OnAllClipsClick(object sender, RoutedEventArgs e) => AppServices.Shell?.ShowMain("gallery");

    void OnReplayToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggle || AppServices.Engine is not { } engine) return;
        if (ReplayToggle.IsChecked == engine.ReplayActive) return;
        _ = Actions.ToggleReplayBufferAsync();
    }

    async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if (AppServices.Engine is not { } engine) return;
        RestartButton.IsEnabled = false;
        try
        {
            if (engine.ReplayActive)
            {
                if (!await engine.RestartAsync())
                    AppServices.Notifier.Show("Нельзя перезапустить во время записи", "Сначала останови запись", NotifyKind.Warning);
            }
            else await engine.StartReplayAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Restart capture failed", ex);
            AppServices.Notifier.Show("Не удалось перезапустить захват", ex.Message, NotifyKind.Error);
        }
        finally { RestartButton.IsEnabled = true; }
    }

    void OnClipClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipInfo clip)
            AppServices.Shell?.OpenEditor(clip.FilePath);
    }

    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    static string PrettyEncoder(string id) => id switch
    {
        "h264_qsv" => "Intel Quick Sync · H.264",
        "hevc_qsv" => "Intel Quick Sync · HEVC",
        "av1_qsv" => "Intel Quick Sync · AV1",
        "h264_amf" => "AMD AMF · H.264",
        "hevc_amf" => "AMD AMF · HEVC",
        "av1_amf" => "AMD AMF · AV1",
        "h264_nvenc" => "NVIDIA NVENC · H.264",
        "hevc_nvenc" => "NVIDIA NVENC · HEVC",
        "av1_nvenc" => "NVIDIA NVENC · AV1",
        "libx264" => "Процессор · x264",
        _ => id,
    };

    static (int W, int H) OutputSize(AppSettings? s)
    {
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        if (w <= 0 || h <= 0) (w, h) = (1920, 1080);
        if (s is { OutputHeight: > 0 } && s.OutputHeight != h)
        {
            var scaled = (int)Math.Round(w * (double)s.OutputHeight / h / 2) * 2;
            return (scaled, s.OutputHeight);
        }
        return (w, h);
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
}

public sealed class ThumbnailConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0 || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 400;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"Thumbnail load failed ({path}): {ex.Message}");
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TimeSpan t ? (t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss")) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClipBar.Core;

namespace ClipBar.UI.Pages;

public partial class SettingsPage : Page
{
    static readonly AppSettings Fallback = new();

    bool _loading;
    bool _saving;
    bool _subscribed;
    DispatcherTimer? _saveTimer;

    public SettingsPage()
    {
        InitializeComponent();
        BuildReplayChips();
        FillVideoCombos();
        FillAudioCombos();
        BuildHotkeyRows();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public bool ShowPageTitle
    {
        get => PageTitle.Visibility == Visibility.Visible;
        set => PageTitle.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ScrollToSection(string section)
    {
        FrameworkElement? header = section.ToLowerInvariant() switch
        {
            "replay" => SecReplay,
            "video" => SecVideo,
            "audio" => SecAudio,
            "hotkeys" => SecHotkeys,
            "folder" => SecFolder,
            "general" => SecGeneral,
            "about" => SecAbout,
            _ => null,
        };
        if (header is null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            try
            {
                var y = header.TransformToAncestor(Body).Transform(new Point(0, 0)).Y;
                Scroller.ScrollToVerticalOffset(Math.Max(0, y - 8));
            }
            catch (Exception ex) { Log.Warn("ScrollToSection: " + ex.Message); }
        });
    }

    static AppSettings S => AppServices.Settings?.Current ?? Fallback;
    static ICaptureEngine? Engine => AppServices.Engine;

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadAll();
            Subscribe();
            _ = LoadEncodersAsync();
            _ = LoadAudioDevicesAsync();
            _ = LoadFreeSpaceAsync();
            _ = LoadAboutAsync();
        }
        catch (Exception ex)
        {
            Log.Error("SettingsPage load failed", ex);
        }
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Unsubscribe();
        FlushPendingSave();
    }

    void LoadAll()
    {
        Guarded(() =>
        {
            LoadReplay();
            LoadVideo();
            LoadAudio();
            LoadHotkeys();
            LoadFolder();
            LoadGeneral();
            LoadAboutStatic();
            UpdateEstimate();
        });
    }

    void Guarded(Action action)
    {
        var was = _loading;
        _loading = true;
        try { action(); }
        finally { _loading = was; }
    }

    void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        if (AppServices.Settings is { } s) s.Changed += OnSettingsChanged;
        if (Engine is { } eng) eng.StateChanged += OnEngineStateChanged;
        if (AppServices.Audio is { } audio) audio.Changed += OnAudioChanged;
    }

    void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        if (AppServices.Settings is { } s) s.Changed -= OnSettingsChanged;
        if (Engine is { } eng) eng.StateChanged -= OnEngineStateChanged;
        if (AppServices.Audio is { } audio) audio.Changed -= OnAudioChanged;
    }

    void Save()
    {
        if (_loading) return;
        _saving = true;
        try { AppServices.Settings?.Save(); }
        catch (Exception ex) { Log.Error("Settings save failed", ex); }
        finally { _saving = false; }
    }

    void SaveDebounced()
    {
        if (_loading) return;
        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void FlushPendingSave()
    {
        if (_saveTimer is { IsEnabled: true })
        {
            _saveTimer.Stop();
            Save();
        }
    }

    void SaveNeedsRestart()
    {
        if (_loading) return;
        Save();
        ShowRestartBarIfCapturing();
    }

    void ShowRestartBarIfCapturing()
    {
        var eng = Engine;
        if (eng is null || eng.State == EngineState.Stopped && !eng.IsRecording) return;
        RestartBarMessage.Text = eng.IsRecording
            ? "Сейчас идёт запись — применить можно будет после её остановки."
            : "Буфер отката на секунду прервётся. Уже сохранённые клипы не пострадают.";
        RestartBar.Visibility = Visibility.Visible;
    }

    async void OnApplyNowClick(object sender, RoutedEventArgs e)
    {
        var eng = Engine;
        if (eng is null) { RestartBar.Visibility = Visibility.Collapsed; return; }
        ApplyNowButton.IsEnabled = false;
        try
        {
            FlushPendingSave();
            var ok = await eng.RestartAsync();
            if (ok)
            {
                RestartBar.Visibility = Visibility.Collapsed;
                AppServices.Notifier?.Show("Настройки применены", "Захват перезапущен с новыми параметрами", NotifyKind.Success);
            }
            else
            {
                AppServices.Notifier?.Show("Нельзя применить во время записи", "Останови запись и нажми «Применить сейчас» ещё раз", NotifyKind.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Engine restart failed", ex);
            AppServices.Notifier?.Show("Не удалось перезапустить захват", ex.Message, NotifyKind.Error);
        }
        finally { ApplyNowButton.IsEnabled = true; }
    }

    void OnRestartBarClose(object sender, RoutedEventArgs e) => RestartBar.Visibility = Visibility.Collapsed;

    void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (_saving) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!IsLoaded) return;
            try { LoadAll(); }
            catch (Exception ex) { Log.Error("SettingsPage refresh failed", ex); }
        });
    }

    void OnEngineStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!IsLoaded || Engine is not { } eng) return;
            Guarded(() => ReplayToggle.IsChecked = eng.ReplayActive || eng.State is EngineState.Starting && S.ReplayEnabled);
            if (eng.State is EngineState.Stopped or EngineState.Starting) RestartBar.Visibility = Visibility.Collapsed;
        });
    }

    void OnAudioChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!IsLoaded) return;
            Guarded(LoadVolumes);
        });
    }

    internal static string FormatBytes(double bytes)
    {
        const double k = 1024;
        if (bytes >= k * k * k) return $"{bytes / (k * k * k):0.#} ГБ";
        if (bytes >= k * k) return $"{bytes / (k * k):0} МБ";
        if (bytes >= k) return $"{bytes / k:0} КБ";
        return $"{bytes:0} Б";
    }

    void UpdateEstimate()
    {
        var s = S;
        var anyAudio = s.RecordSystemAudio || s.RecordMic;
        var tracks = !anyAudio ? 0 : !s.SeparateAudioTracks ? 1 : 1 + (s.RecordSystemAudio ? 1 : 0) + (s.RecordMic ? 1 : 0);
        var kbps = s.VideoBitrateKbps + s.AudioBitrateKbps * tracks;
        var bytes = kbps * 1000.0 / 8 * s.ReplaySeconds;
        ReplayEstimateText.Text = $"≈ {FormatBytes(bytes)} на диске за {Actions.FormatDuration(TimeSpan.FromSeconds(s.ReplaySeconds))} при текущих настройках видео и звука";
    }

    sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    static void Select<T>(ComboBox combo, Func<T, bool> match, string? missingLabel = null, T? missingValue = default)
    {
        for (var i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is Choice<T> c && match(c.Value)) { combo.SelectedIndex = i; return; }
        if (missingLabel is not null && missingValue is not null)
        {
            combo.Items.Add(new Choice<T>(missingLabel, missingValue));
            combo.SelectedIndex = combo.Items.Count - 1;
        }
        else if (combo.Items.Count > 0 && combo.SelectedIndex < 0) combo.SelectedIndex = 0;
    }

    static T? Selected<T>(ComboBox combo) => combo.SelectedItem is Choice<T> c ? c.Value : default;

    static void OpenInExplorer(string folder)
    {
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Open folder failed: " + folder, ex);
            AppServices.Notifier?.Show("Не удалось открыть папку", folder, NotifyKind.Error);
        }
    }
}

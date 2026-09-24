using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClipBar.Core;

public enum EngineState { Stopped, Starting, Buffering, Error }

public sealed record EncoderInfo(string Id, string DisplayName, bool Hardware);

public interface ICaptureEngine : IDisposable
{
    EngineState State { get; }
    bool ReplayActive { get; }
    bool IsRecording { get; }
    TimeSpan RecordingElapsed { get; }
    TimeSpan BufferedDuration { get; }
    string? ActiveEncoder { get; }
    string? LastError { get; }

    event EventHandler? StateChanged;

    Task StartReplayAsync();
    Task StopReplayAsync();
    Task<bool> RestartAsync();

    Task<string> SaveReplayAsync(TimeSpan? duration = null);

    Task StartRecordingAsync();
    Task<string> StopRecordingAsync();

    Task<string> TakeScreenshotAsync();

    Task<IReadOnlyList<EncoderInfo>> GetAvailableEncodersAsync();
}

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

public interface IAudioHub : IDisposable
{
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
    IReadOnlyList<AudioDeviceInfo> GetInputDevices();

    float SystemLevel { get; }
    float MicLevel { get; }

    float SystemVolume { get; set; }
    float MicVolume { get; set; }
    bool MicMuted { get; set; }

    event EventHandler? Changed;

    IAudioPipeSession CreateSession(bool includeSystem, bool includeMic);

    void SetMonitoring(bool enabled);
}

public interface IAudioPipeSession : IDisposable
{
    string? SystemPipePath { get; }
    string? MicPipePath { get; }
    int SampleRate { get; }
    int Channels { get; }
    string SampleFormat { get; }
    void Start();
}

public interface IHotkeyService : IDisposable
{
    void Initialize();
    IReadOnlyList<HotkeyAction> Apply(IReadOnlyDictionary<HotkeyAction, HotkeyBinding> bindings);
    IReadOnlyList<HotkeyAction> FailedActions { get; }
    bool Suspended { get; set; }
    event EventHandler<HotkeyAction>? Pressed;
}

public sealed class ClipInfo : INotifyPropertyChanged
{
    public required string FilePath { get; set; }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public string Title => System.IO.Path.GetFileNameWithoutExtension(FilePath);
    public bool IsScreenshot { get; init; }
    public DateTime CreatedAt { get; set; }
    public long SizeBytes { get; set; }

    TimeSpan? _duration; string? _thumb; int _w, _h, _tracks;
    public TimeSpan? Duration { get => _duration; set => Set(ref _duration, value); }
    public string? ThumbnailPath { get => _thumb; set => Set(ref _thumb, value); }
    public int Width { get => _w; set => Set(ref _w, value); }
    public int Height { get => _h; set => Set(ref _h, value); }
    public int AudioTracks { get => _tracks; set => Set(ref _tracks, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public interface IClipLibrary
{
    ObservableCollection<ClipInfo> Items { get; }
    Task RefreshAsync();
    Task<ClipInfo?> AddAsync(string path);
    Task DeleteAsync(ClipInfo clip);
    Task<bool> RenameAsync(ClipInfo clip, string newTitle);
    void ShowInExplorer(ClipInfo clip);
}

public enum NotifyKind { Info, Success, Warning, Error, Recording }

public interface INotifier
{
    void Show(string title, string message, NotifyKind kind = NotifyKind.Info, string? clipPath = null);

    void ShowAction(string title, string message, NotifyKind kind, string hint, Action onClick);
}

public interface IShell
{
    void ShowMain(string? page = null);
    void OpenEditor(string clipPath);
    void ToggleOverlay();
    void ShowOverlay(bool gallery = false);
    void StartRegionScreenshot();
    void HideOverlay();
    void ExitApp();
}

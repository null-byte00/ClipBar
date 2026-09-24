using System.IO;
using System.IO.Pipes;
using ClipBar.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClipBar.Audio;

public sealed class AudioHub : IAudioHub
{
    const int RingSeconds = 2;
    const int RingCapacityFrames = AudioFormatConverter.TargetSampleRate * RingSeconds;
    const int PumpChunkMs = 15;

    readonly SettingsService _settings;
    readonly object _gate = new();

    SourceState? _system;
    SourceState? _mic;
    bool _monitoring;
    int _sessionCount;

    volatile float _systemVolume;
    volatile float _micVolume;
    volatile bool _micMuted;

    public AudioHub(SettingsService settings)
    {
        _settings = settings;
        var s = settings.Current;
        _systemVolume = Math.Clamp(s.SystemVolume, 0f, 2f);
        _micVolume = Math.Clamp(s.MicVolume, 0f, 2f);
        _micMuted = s.MicMuted;
    }

    public float SystemVolume
    {
        get => _systemVolume;
        set { _systemVolume = Math.Clamp(value, 0f, 2f); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public float MicVolume
    {
        get => _micVolume;
        set { _micVolume = Math.Clamp(value, 0f, 2f); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public bool MicMuted
    {
        get => _micMuted;
        set { _micMuted = value; Changed?.Invoke(this, EventArgs.Empty); }
    }

    public float SystemLevel => _system?.Level ?? 0f;
    public float MicLevel => _mic?.Level ?? 0f;

    public event EventHandler? Changed;

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => EnumerateDevices(DataFlow.Render);
    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => EnumerateDevices(DataFlow.Capture);

    static List<AudioDeviceInfo> EnumerateDevices(DataFlow flow)
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try { defaultId = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID; }
            catch { }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try { list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId)); }
                catch (Exception ex) { Log.Error($"AudioHub: failed to read device info: {ex.Message}"); }
                finally { device.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"AudioHub: failed to enumerate {flow} devices: {ex.Message}");
        }
        return list;
    }

    public void SetMonitoring(bool enabled)
    {
        lock (_gate)
        {
            if (_monitoring == enabled) return;
            _monitoring = enabled;
            RefreshSourcesLocked();
        }
    }

    public IAudioPipeSession CreateSession(bool includeSystem, bool includeMic)
    {
        lock (_gate)
        {
            _sessionCount++;
            RefreshSourcesLocked();
            return new AudioPipeSession(this, includeSystem ? _system : null, includeMic ? _mic : null);
        }
    }

    void OnSessionDisposed()
    {
        lock (_gate)
        {
            _sessionCount = Math.Max(0, _sessionCount - 1);
            RefreshSourcesLocked();
        }
    }

    void RefreshSourcesLocked()
    {
        bool wantAny = _sessionCount > 0 || _monitoring;
        var settings = _settings.Current;

        bool wantSystem = wantAny && settings.RecordSystemAudio;
        if (!wantSystem) { _system?.Dispose(); _system = null; }
        else if (_system is null || _system.DeviceId != settings.SystemDeviceId)
        {
            _system?.Dispose();
            _system = SourceState.StartLoopback(settings.SystemDeviceId, () => _systemVolume);
        }

        bool wantMic = wantAny && settings.RecordMic;
        if (!wantMic) { _mic?.Dispose(); _mic = null; }
        else if (_mic is null || _mic.DeviceId != settings.MicDeviceId)
        {
            _mic?.Dispose();
            _mic = SourceState.StartMic(settings.MicDeviceId, () => _micVolume, () => _micMuted);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _system?.Dispose(); _system = null;
            _mic?.Dispose(); _mic = null;
            _sessionCount = 0;
            _monitoring = false;
        }
    }

    sealed class SourceState : IDisposable
    {
        public volatile bool Available;
        float _levelPeak;

        readonly object _subsLock = new();
        FloatRing[] _subscribers = [];

        IWaveIn? _capture;
        AudioFormatConverter? _converter;
        readonly Func<float> _gain;
        readonly Func<bool>? _mutedFn;

        public string? DeviceId { get; }

        SourceState(string? deviceId, Func<float> gain, Func<bool>? mutedFn)
        {
            DeviceId = deviceId;
            _gain = gain;
            _mutedFn = mutedFn;
        }

        public float Level => Volatile.Read(ref _levelPeak);

        public FloatRing Subscribe()
        {
            var ring = new FloatRing(RingCapacityFrames);
            lock (_subsLock) _subscribers = [.. _subscribers, ring];
            return ring;
        }

        public void Unsubscribe(FloatRing ring)
        {
            lock (_subsLock) _subscribers = _subscribers.Where(r => !ReferenceEquals(r, ring)).ToArray();
        }

        public static SourceState StartLoopback(string? deviceId, Func<float> gain)
        {
            var state = new SourceState(deviceId, gain, null);
            state.Start(deviceId, isLoopback: true);
            return state;
        }

        public static SourceState StartMic(string? deviceId, Func<float> gain, Func<bool> mutedFn)
        {
            var state = new SourceState(deviceId, gain, mutedFn);
            state.Start(deviceId, isLoopback: false);
            return state;
        }

        void Start(string? deviceId, bool isLoopback)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var flow = isLoopback ? DataFlow.Render : DataFlow.Capture;
                MMDevice device = deviceId is null
                    ? enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
                    : enumerator.GetDevice(deviceId);

#pragma warning disable CS0618 // NAudio 3.1's simple WasapiLoopbackCapture/WasapiCapture are deprecated in favor of

                IWaveIn capture = isLoopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
#pragma warning restore CS0618
                _converter = new AudioFormatConverter(capture.WaveFormat);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception != null)
                        Log.Error($"AudioHub: capture stopped unexpectedly: {e.Exception.Message}");
                    Available = false;
                };
                capture.StartRecording();
                _capture = capture;
                Available = true;
            }
            catch (Exception ex)
            {
                Log.Error($"AudioHub: failed to start {(isLoopback ? "system" : "mic")} capture: {ex.Message}");
                Available = false;
            }
        }

        void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            try
            {
                if (_converter is null) return;
                int maxFrames = (int)Math.Ceiling((long)e.BytesRecorded / Math.Max(1, _converter.BlockAlign) *
                                 (double)AudioFormatConverter.TargetSampleRate / SafeSrcRateHint()) + 2;
                Span<float> scratch = maxFrames * 2 <= 8192
                    ? stackalloc float[maxFrames * 2]
                    : new float[maxFrames * 2];
                int frames = _converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded), scratch);
                if (frames <= 0) return;
                var pcm = scratch[..(frames * 2)];

                float gain = Math.Clamp(_gain(), 0f, 2f);
                bool muted = _mutedFn?.Invoke() ?? false;

                float peak = 0f;
                for (int i = 0; i < pcm.Length; i++)
                {
                    pcm[i] *= gain;
                    float a = Math.Abs(pcm[i]);
                    if (a > peak) peak = a;
                }
                var prev = Volatile.Read(ref _levelPeak);
                Volatile.Write(ref _levelPeak, peak > prev ? peak : prev * 0.85f);

                if (!muted)
                    foreach (var ring in _subscribers) ring.Write(pcm);
            }
            catch (Exception ex)
            {
                Log.Error($"AudioHub: error processing captured audio: {ex.Message}");
            }
        }

        int SafeSrcRateHint() => _capture?.WaveFormat.SampleRate is > 0 ? _capture.WaveFormat.SampleRate : AudioFormatConverter.TargetSampleRate;

        public void Dispose()
        {
            try { if (_capture != null) { _capture.DataAvailable -= OnDataAvailable; _capture.StopRecording(); _capture.Dispose(); } }
            catch (Exception ex) { Log.Error($"AudioHub: error stopping capture: {ex.Message}"); }
            Available = false;
            Volatile.Write(ref _levelPeak, 0f);
        }
    }

    sealed class AudioPipeSession : IAudioPipeSession
    {
        readonly AudioHub _hub;
        readonly PipePump? _system;
        readonly PipePump? _mic;
        int _disposed;

        public string? SystemPipePath { get; }
        public string? MicPipePath { get; }
        public int SampleRate => AudioFormatConverter.TargetSampleRate;
        public int Channels => 2;
        public string SampleFormat => "f32le";

        public AudioPipeSession(AudioHub hub, SourceState? system, SourceState? mic)
        {
            _hub = hub;
            if (system != null)
            {
                var name = "clipbar_sys_" + RandomHex();
                SystemPipePath = @"\\.\pipe\" + name;
                _system = new PipePump(name, system);
            }
            if (mic != null)
            {
                var name = "clipbar_mic_" + RandomHex();
                MicPipePath = @"\\.\pipe\" + name;
                _mic = new PipePump(name, mic);
            }
        }

        static string RandomHex() => Random.Shared.Next(0, 0xFFFFFF).ToString("x6");

        public void Start()
        {
            _system?.Start();
            _mic?.Start();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _system?.Dispose();
            _mic?.Dispose();
            _hub.OnSessionDisposed();
        }
    }

    sealed class PipePump : IDisposable
    {
        const int FramesPerChunk = AudioFormatConverter.TargetSampleRate * PumpChunkMs / 1000;

        readonly NamedPipeServerStream _pipe;
        readonly SourceState _source;
        readonly FloatRing _ring;
        readonly CancellationTokenSource _cts = new();
        Task? _pumpTask;
        int _disposed;

        public PipePump(string pipeName, SourceState source)
        {
            _source = source;
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _ring = source.Subscribe();
        }

        public void Start()
        {
            _pumpTask = Task.Run(PumpAsync);
        }

        async Task PumpAsync()
        {
            var token = _cts.Token;
            try
            {
                await _pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Error($"AudioHub: pipe wait-for-connection failed: {ex.Message}");
                return;
            }

            var buf = new float[FramesPerChunk * 2];
            var bytes = new byte[buf.Length * sizeof(float)];
            var delay = TimeSpan.FromMilliseconds(PumpChunkMs);

            IntPtr mmcss = AudioNative.EnterProAudio();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int frames = _source.Available ? _ring.Read(buf) : 0;
                    if (frames < FramesPerChunk)
                        Array.Clear(buf, frames * 2, (FramesPerChunk - frames) * 2);

                    Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length);
                    try
                    {
                        await _pipe.WriteAsync(bytes, token).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    try { await Task.Delay(delay, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"AudioHub: pipe pump error: {ex.Message}");
            }
            finally
            {
                AudioNative.LeaveMmcss(mmcss);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _source.Unsubscribe(_ring);
            try { _cts.Cancel(); } catch { }
            bool finished = false;
            try { finished = _pumpTask?.Wait(1500) ?? true; } catch { finished = true; }
            try { if (_pipe.IsConnected) _pipe.Disconnect(); } catch { }
            try { _pipe.Dispose(); } catch { }
            if (finished) _cts.Dispose();
            else _pumpTask!.ContinueWith(_ => _cts.Dispose(), TaskScheduler.Default);
        }
    }
}

using System.IO;
using ClipBar.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClipBar.Editor;

public sealed class PreviewAudioMixer : IDisposable
{
    readonly WaveFileReader[] _readers;
    readonly TrackGain[] _tracks;
    readonly string[] _tempFiles;
#pragma warning disable CS0618
    readonly WasapiOut _out;
#pragma warning restore CS0618
    readonly object _gate = new();
    bool _disposed;

    public bool Separate { get; }

    PreviewAudioMixer(string[] files, bool separate)
    {
        _tempFiles = files;
        Separate = separate;
        _readers = files.Select(f => new WaveFileReader(f)).ToArray();
        _tracks = _readers.Select(r => new TrackGain(r.ToSampleProvider())).ToArray();
        var mixer = new MixingSampleProvider(_tracks) { ReadFully = true };
        var limited = new SoftLimiter(mixer);
#pragma warning disable CS0618
        _out = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, useEventSync: true, latency: 60);
#pragma warning restore CS0618
        _out.Init(new Locked(limited, _gate));
    }

    sealed class Locked(ISampleProvider source, object gate) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(Span<float> buffer) { lock (gate) return source.Read(buffer); }
    }

    public static async Task<PreviewAudioMixer?> CreateAsync(string clipPath, ClipMediaInfo info, CancellationToken ct)
    {
        if (!info.HasAudio) return null;
        Directory.CreateDirectory(AppPaths.TempDir);
        foreach (var old in Directory.EnumerateFiles(AppPaths.TempDir, "prev_*.wav"))
            if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddMinutes(-30)) { try { File.Delete(old); } catch { } }
        var id = Guid.NewGuid().ToString("N")[..8];
        var separate = info.HasSeparateTracks;
        string[] files = separate
            ? [Path.Combine(AppPaths.TempDir, $"prev_{id}_sys.wav"), Path.Combine(AppPaths.TempDir, $"prev_{id}_mic.wav")]
            : [Path.Combine(AppPaths.TempDir, $"prev_{id}_mix.wav")];
        var pcm = "-vn -ac 2 -ar 48000 -c:a pcm_f32le";
        var args = separate
            ? $"-hide_banner -loglevel error -y -i {Ffmpeg.Q(clipPath)} -map 0:a:1 {pcm} {Ffmpeg.Q(files[0])} -map 0:a:2 {pcm} {Ffmpeg.Q(files[1])}"
            : $"-hide_banner -loglevel error -y -i {Ffmpeg.Q(clipPath)} -map 0:a:0 {pcm} {Ffmpeg.Q(files[0])}";
        try
        {
            var r = await Ffmpeg.RunAsync(args, ct);
            if (!r.Ok || files.Any(f => !File.Exists(f))) throw new IOException(r.StdErr.Trim());
            return new PreviewAudioMixer(files, separate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Preview mixer unavailable for {clipPath}: {ex.Message}");
            foreach (var f in files) { try { File.Delete(f); } catch { } }
            return null;
        }
        catch
        {
            foreach (var f in files) { try { File.Delete(f); } catch { } }
            throw;
        }
    }

    public void SetGains(float system, float mic, float master)
    {
        if (Separate) { _tracks[0].Gain = system; _tracks[1].Gain = mic; }
        else _tracks[0].Gain = master;
    }

    public bool Muted { set { foreach (var t in _tracks) t.Muted = value; } }

    public TimeSpan Position { get { lock (_gate) return _readers[0].CurrentTime; } }

    public void Seek(TimeSpan t)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var r in _readers)
                r.CurrentTime = t < TimeSpan.Zero ? TimeSpan.Zero : t > r.TotalTime ? r.TotalTime : t;
        }
    }

    public void Play(TimeSpan from)
    {
        if (_disposed) return;
        Seek(from);
        try { _out.Play(); } catch (Exception ex) { Log.Warn("Preview audio play failed: " + ex.Message); }
    }

    public void Pause()
    {
        if (_disposed) return;
        try { _out.Pause(); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { _out.Stop(); } catch { }
        try { _out.Dispose(); } catch { }

        lock (_gate)
        {
            foreach (var r in _readers) { try { r.Dispose(); } catch { } }
        }
        foreach (var f in _tempFiles) { try { File.Delete(f); } catch { } }
    }

    sealed class TrackGain(ISampleProvider source) : ISampleProvider
    {
        public volatile float Gain = 1f;
        public volatile bool Muted;
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(Span<float> buffer)
        {
            var n = source.Read(buffer);
            var g = Muted ? 0f : Gain;
            if (g != 1f)
                for (var i = 0; i < n; i++) buffer[i] *= g;
            return n;
        }
    }

    sealed class SoftLimiter(ISampleProvider source) : ISampleProvider
    {
        const float Knee = 0.89f;
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(Span<float> buffer)
        {
            var n = source.Read(buffer);
            for (var i = 0; i < n; i++)
            {
                var x = buffer[i];
                var a = Math.Abs(x);
                if (a > Knee)
                    buffer[i] = Math.Sign(x) * (Knee + (1 - Knee) * MathF.Tanh((a - Knee) / (1 - Knee)));
            }
            return n;
        }
    }
}

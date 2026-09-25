using System.Diagnostics;
using System.IO;
using System.Text;
using ClipBar.Core;

namespace ClipBar.Capture;

public sealed class CaptureEngine : ICaptureEngine
{
    readonly SettingsService _settings;
    readonly IAudioHub _audio;
    readonly EncoderProbe _encoderProbe;
    readonly SegmentStore _segments = new();
    readonly SemaphoreSlim _lifecycle = new(1, 1);
    readonly object _stateLock = new();

    Process? _process;
    IAudioPipeSession? _audioSession;
    StderrRing? _stderr;
    Timer? _tailTimer;
    long _tailPos;
    int _epoch;
    string? _listPath;
    string? _activeEncoder;
    volatile bool _wantRunning;

    int _recordingStartIndex;
    SegmentHold? _recordingHold;
    Stopwatch? _recordingStopwatch;

    EngineState _state = EngineState.Stopped;
    bool _replayActive;
    bool _isRecording;
    string? _lastError;
    bool _disposed;
    bool _restartScheduled;

    public CaptureEngine(SettingsService settings, IAudioHub audio)
    {
        _settings = settings;
        _audio = audio;
        _encoderProbe = new EncoderProbe(settings);
    }

    public EngineState State { get { lock (_stateLock) return _state; } }
    public bool ReplayActive { get { lock (_stateLock) return _replayActive; } }
    public bool IsRecording { get { lock (_stateLock) return _isRecording; } }
    public string? ActiveEncoder { get { lock (_stateLock) return _activeEncoder; } }
    public string? LastError { get { lock (_stateLock) return _lastError; } }

    public TimeSpan RecordingElapsed => _recordingStopwatch?.Elapsed ?? TimeSpan.Zero;

    public TimeSpan BufferedDuration => TimeSpan.FromSeconds(_segments.DurationOfEpoch(_epoch));

    public event EventHandler? StateChanged;

    void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("StateChanged handler threw", ex); }
    }

    void SetState(EngineState state, string? error = null)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = _state != state || _lastError != error;
            _state = state;
            _lastError = error;
        }
        if (changed)
        {
            Log.Info($"CaptureEngine state -> {state}" + (error is null ? "" : $" ({error})"));
            RaiseStateChanged();
        }
    }

    void SetFlags(bool? replayActive = null, bool? isRecording = null)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = false;
            if (replayActive is { } r && r != _replayActive) { _replayActive = r; changed = true; }
            if (isRecording is { } rec && rec != _isRecording) { _isRecording = rec; changed = true; }
        }
        if (changed) RaiseStateChanged();
    }

    public async Task StartReplayAsync()
    {
        if (ReplayActive) return;
        await _lifecycle.WaitAsync();
        try
        {
            SetFlags(replayActive: true);
            _wantRunning = true;
            await EnsureCaptureRunningAsync();
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopReplayAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            SetFlags(replayActive: false);
            if (IsRecording) return;
            _wantRunning = false;
            await StopCaptureProcessAsync();
            ClearAllSegments();
            SetState(EngineState.Stopped);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<bool> RestartAsync()
    {
        if (IsRecording) return false;
        await _lifecycle.WaitAsync();
        try
        {
            var shouldRun = ReplayActive;
            _epoch++;
            await StopCaptureProcessAsync();
            if (shouldRun)
            {
                await StartCaptureProcessAsync();
            }
            else
            {
                SetState(EngineState.Stopped);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("RestartAsync failed", ex);
            SetState(EngineState.Error, ex.Message);
            return false;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<string> SaveReplayAsync(TimeSpan? duration = null)
    {
        if (!ReplayActive || _process is null)
            throw new InvalidOperationException("Буфер отката выключен");

        var wanted = duration ?? TimeSpan.FromSeconds(_settings.Current.ReplaySeconds);
        await _segments.WaitForIndexAsync(_segments.LastIndex + 1, TimeSpan.FromSeconds(2));
        CatchUpSegments();
        var (segs, hold) = _segments.HoldNewest(_epoch, wanted.TotalSeconds);
        if (segs.Count == 0 || hold is null)
            throw new InvalidOperationException("Буфер пуст, подождите немного");

        try
        {
            var outPath = BuildOutputPath(".mp4");
            await ConcatAsync(segs, outPath);
            Log.Info($"Saved replay ({segs.Count} segs, ~{segs.Sum(s => s.Duration):0.#}s) -> {outPath}");

            _segments.Release(hold);
            foreach (var s in _segments.TakeUpTo(segs[^1].Index)) TryDeleteFile(s.Path);
            RaiseStateChanged();
            return outPath;
        }
        finally { _segments.Release(hold); }
    }

    public async Task StartRecordingAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            _wantRunning = true;
            await EnsureCaptureRunningAsync();

            CatchUpSegments();
            _recordingStartIndex = _segments.LastIndex + 1;
            _recordingHold = _segments.Hold(_recordingStartIndex, int.MaxValue);
            _recordingStopwatch = Stopwatch.StartNew();
            SetFlags(isRecording: true);
            Log.Info($"Recording started at segment {_recordingStartIndex}");
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<string> StopRecordingAsync()
    {
        _recordingStopwatch?.Stop();
        try
        {
            await _segments.WaitForIndexAsync(_segments.LastIndex + 1, TimeSpan.FromSeconds(2));
            CatchUpSegments();
            var segs = _segments.Range(_recordingStartIndex, _segments.LastIndex);
            if (segs.Count == 0)
                throw new InvalidOperationException("Не удалось сохранить запись: сегменты не найдены");

            var outPath = BuildOutputPath(".mp4");
            await ConcatAsync(segs, outPath);
            Log.Info($"Recording saved ({segs.Count} segs, ~{segs.Sum(s => s.Duration):0.#}s) -> {outPath}");
            return outPath;
        }
        finally
        {
            if (_recordingHold is { } h) _segments.Release(h);
            _recordingHold = null;
            SetFlags(isRecording: false);

            await _lifecycle.WaitAsync();
            try
            {
                if (!ReplayActive)
                {
                    _wantRunning = false;
                    await StopCaptureProcessAsync();
                    ClearAllSegments();
                    SetState(EngineState.Stopped);
                }
            }
            finally { _lifecycle.Release(); }
        }
    }

    public Task<string> TakeScreenshotAsync()
    {
        var path = BuildOutputPath(".png");
        return Task.Run(() =>
        {
            try { ScreenGrabber.SavePng(_settings.Current.MonitorIndex, path); }
            catch
            {
                try { File.Delete(path); } catch { }
                throw;
            }
            return path;
        });
    }

    public Task<IReadOnlyList<EncoderInfo>> GetAvailableEncodersAsync() => _encoderProbe.GetInfosAsync();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _tailTimer?.Dispose();
            var p = _process;
            if (p is not null && !p.HasExited)
                Ffmpeg.StopGracefullyAsync(p, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            p?.Dispose();
        }
        catch (Exception ex) { Log.Error("CaptureEngine dispose (process) failed", ex); }
        try { _audioSession?.Dispose(); }
        catch (Exception ex) { Log.Error("CaptureEngine dispose (audio session) failed", ex); }
        _lifecycle.Dispose();
    }

    async Task EnsureCaptureRunningAsync()
    {
        if (_process is { HasExited: false }) return;
        await StartCaptureProcessAsync();
    }

    async Task StartCaptureProcessAsync()
    {
        SetState(EngineState.Starting);
        try
        {
            var s = _settings.Current;
            var working = await _encoderProbe.GetWorkingAsync();
            if (working.Count == 0)
                throw new InvalidOperationException("Ни один видеокодер не работает на этом ПК");

            WorkingEncoder chosen;
            if (s.Encoder == "auto")
            {
                chosen = working[0];
            }
            else
            {
                var found = working.FirstOrDefault(w => w.Id == s.Encoder);
                if (found is null)
                {
                    Log.Warn($"Configured encoder '{s.Encoder}' is not available, falling back to auto ({working[0].Id})");
                    chosen = working[0];
                }
                else chosen = found;
            }

            var nativeHeight = SafeNativeHeight(s.MonitorIndex);

            var audioSession = _audio.CreateSession(s.RecordSystemAudio, s.RecordMic);
            var layout = new AudioLayout(audioSession.SystemPipePath, audioSession.MicPipePath, s.SeparateAudioTracks);

            var t0us = CaptureCommand.UnixMicros(DateTime.UtcNow);
            Directory.CreateDirectory(AppPaths.TempDir);
            Directory.CreateDirectory(AppPaths.BufferDir);
            if (_segments.Count == 0) WipeStaleBuffer();
            var startNumber = _segments.LastIndex + 1;
            if (startNumber < 0) startNumber = 0;
            var listPath = Path.Combine(AppPaths.TempDir, $"segments_{Environment.ProcessId}_{_epoch + 1}.csv");
            _listPath = listPath;
            try { File.Delete(listPath); } catch { }

            var args = CaptureCommand.BuildCapture(s, chosen.Spec, chosen.Variant, layout, t0us,
                nativeHeight, startNumber, listPath, AppPaths.BufferDir);
            Log.Info($"Starting capture: {AppPaths.FfmpegPath} {args}");

            var stderr = new StderrRing(200);
            var epoch = ++_epoch;
            var logged = 0;
            var process = Ffmpeg.StartLongRunning(args, line =>
            {
                stderr.Add(line);
                if (Interlocked.Increment(ref logged) <= 60 ||
                    line.Contains("rror", StringComparison.Ordinal) || line.Contains("arning", StringComparison.Ordinal))
                    Log.Info("ffmpeg: " + line);
            });
            process.Exited += (_, _) => OnProcessExited(epoch, process);

            audioSession.Start();

            _process = process;
            _audioSession = audioSession;
            _stderr = stderr;
            _activeEncoder = chosen.Id;
            _tailPos = 0;
            _tailTimer?.Dispose();
            _tailTimer = new Timer(_ => TailSegmentList(listPath, epoch), null, 250, 250);

            lock (_stateLock) _activeEncoder = chosen.Id;
            SetState(EngineState.Starting);
        }
        catch (Exception ex)
        {
            Log.Error("StartCaptureProcessAsync failed", ex);
            lock (_stateLock) _activeEncoder = null;
            SetState(EngineState.Error, ex.Message);
            throw;
        }
    }

    async Task StopCaptureProcessAsync()
    {
        _tailTimer?.Dispose();
        _tailTimer = null;

        var p = _process;
        _process = null;
        if (p is not null)
        {
            try { await Ffmpeg.StopGracefullyAsync(p, TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { Log.Error("Stopping capture process failed", ex); }
            finally { p.Dispose(); }
        }

        var session = _audioSession;
        _audioSession = null;
        try { session?.Dispose(); }
        catch (Exception ex) { Log.Error("Disposing audio session failed", ex); }

        lock (_stateLock) _activeEncoder = null;
        _stderr = null;
    }

    void OnProcessExited(int epoch, Process process)
    {
        try
        {
            if (epoch != _epoch || !ReferenceEquals(process, _process)) return;
            if (!_wantRunning) return;

            var err = _stderr?.LastError() ?? "ffmpeg завершился неожиданно";
            Log.Error($"Capture process exited unexpectedly: {err}");
            SetState(EngineState.Error, err);

            if (_restartScheduled) return;
            _restartScheduled = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    await _lifecycle.WaitAsync();
                    try
                    {
                        if (!_wantRunning || !ReferenceEquals(process, _process)) return;
                        await StartCaptureProcessAsync();
                    }
                    finally { _lifecycle.Release(); }
                }
                catch (Exception ex)
                {
                    Log.Error("Auto-restart after capture crash failed", ex);
                    SetState(EngineState.Error, ex.Message);
                }
                finally { _restartScheduled = false; }
            });
        }
        catch (Exception ex)
        {
            Log.Error("OnProcessExited handler failed", ex);
        }
    }

    void TailSegmentList(string listPath, int epoch)
    {
        if (epoch != _epoch) return;
        try
        {
            if (!File.Exists(listPath)) return;
            using var fs = new FileStream(listPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _tailPos) _tailPos = 0;
            fs.Seek(_tailPos, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            var any = false;
            while ((line = reader.ReadLine()) is not null)
            {
                any = true;
                if (!SegmentStore.TryParseCsvLine(line, out var name, out var start, out var end)) continue;
                if (!SegmentStore.TryParseIndex(name, out var index)) continue;
                var path = Path.Combine(AppPaths.BufferDir, name);
                _segments.Add(new SegmentInfo(index, epoch, path, start, end, DateTime.UtcNow));
            }
            _tailPos = fs.Position;

            if (any)
            {
                if (State == EngineState.Starting) SetState(EngineState.Buffering);
                PruneExpired(epoch);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading segment list failed: {ex.Message}");
        }
    }

    // Защита от рассинхрона: перед сохранением записи/отката дочитываем ВЕСЬ список сегментов
    // напрямую (не полагаясь на tail-таймер и _tailPos) и добавляем всё, что реально есть на диске.
    // Иначе, если трекер отстал, LastIndex «зависает» и запись падает с «сегменты не найдены».
    void CatchUpSegments()
    {
        var lp = _listPath;
        if (lp is null || !File.Exists(lp)) return;
        try
        {
            // Тот же шаринг, что и tail — ffmpeg держит файл открытым на запись.
            using var fs = new FileStream(lp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!SegmentStore.TryParseCsvLine(line, out var name, out var start, out var end)) continue;
                if (!SegmentStore.TryParseIndex(name, out var index)) continue;
                var path = Path.Combine(AppPaths.BufferDir, name);
                if (File.Exists(path)) _segments.Add(new SegmentInfo(index, _epoch, path, start, end, DateTime.UtcNow));
            }
        }
        catch (Exception ex) { Log.Warn($"CatchUpSegments failed: {ex.Message}"); }
    }

    void PruneExpired(int epoch)
    {
        try
        {
            var keepSeconds = Math.Max(_settings.Current.ReplaySeconds, 15);
            var expired = _segments.TakeExpired(epoch, keepSeconds);
            foreach (var s in expired) TryDeleteFile(s.Path);
        }
        catch (Exception ex) { Log.Warn($"Pruning expired segments failed: {ex.Message}"); }
    }

    void ClearAllSegments()
    {
        foreach (var s in _segments.TakeAll()) TryDeleteFile(s.Path);
    }

    static void WipeStaleBuffer()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.BufferDir, "*.ts")) TryDeleteFile(f);
            foreach (var f in Directory.EnumerateFiles(AppPaths.TempDir, "segments*.csv"))
                try { File.Delete(f); } catch { }
        }
        catch (Exception ex) { Log.Warn("Wiping stale buffer failed: " + ex.Message); }
    }

    static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Log.Warn($"Could not delete segment {path}: {ex.Message}"); }
    }

    static int SafeNativeHeight(int monitorIndex)
    {
        try { return ScreenGrabber.GetMonitor(monitorIndex).Height; }
        catch { try { return ScreenGrabber.GetMonitor(0).Height; } catch { return 0; } }
    }

    static async Task ConcatAsync(List<SegmentInfo> segs, string outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        Directory.CreateDirectory(AppPaths.TempDir);
        var listPath = Path.Combine(AppPaths.TempDir, $"concat_{Guid.NewGuid():N}.txt");
        try
        {
            var sb = new StringBuilder();
            foreach (var s in segs)
                sb.Append("file '").Append(s.Path.Replace("'", "'\\''")).Append("'\n");
            await File.WriteAllTextAsync(listPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var titles = await AudioTrackCountAsync(segs[0].Path) == 3 ? TrackTitleArgs("Микс", "Система", "Микрофон") : "";
            var args = $"-hide_banner -loglevel error -y -f concat -safe 0 -i {Ffmpeg.Q(listPath)} " +
                       $"-map 0 -c copy {titles}-movflags +faststart {Ffmpeg.Q(outPath)}";
            var r = await Ffmpeg.RunAsync(args);
            if (!r.Ok)
            {
                var detail = LastNonEmptyLine(r.StdErr);
                throw new InvalidOperationException($"Не удалось склеить видео: {detail}");
            }
        }
        catch
        {
            try { File.Delete(outPath); } catch { }
            throw;
        }
        finally
        {
            try { File.Delete(listPath); } catch { }
        }
    }

    static string TrackTitleArgs(params string[] names)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < names.Length; i++)
            sb.Append($"-metadata:s:a:{i} title=\"{names[i]}\" -metadata:s:a:{i} handler_name=\"{names[i]}\" ");
        return sb.ToString();
    }

    static async Task<int> AudioTrackCountAsync(string segmentPath)
    {
        try
        {
            var r = await Ffmpeg.ProbeAsync($"-v error -select_streams a -show_entries stream=index -of csv=p=0 {Ffmpeg.Q(segmentPath)}");
            return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Count();
        }
        catch { return 0; }
    }

    static string LastNonEmptyLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault() ?? "неизвестная ошибка ffmpeg";
    }

    readonly object _nameLock = new();

    string BuildOutputPath(string extension)
    {
        var s = _settings.Current;
        Directory.CreateDirectory(s.ClipsFolder);

        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        var name = s.UseAppNameInFileName ? $"{ForegroundApp.GetLabel()} {stamp}" : stamp;
        name = Sanitize(name);

        lock (_nameLock)
        {
            var path = Path.Combine(s.ClipsFolder, name + extension);
            for (var n = 2; ; n++)
            {
                try
                {
                    using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
                    return path;
                }
                catch (IOException) when (File.Exists(path))
                {
                    path = Path.Combine(s.ClipsFolder, $"{name} ({n}){extension}");
                }
            }
        }
    }

    static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (Array.IndexOf(invalid, ch) < 0) sb.Append(ch);
        var r = sb.ToString().Trim();
        return r.Length == 0 ? "clip" : r;
    }
}

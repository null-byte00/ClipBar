using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipBar.Core;

namespace ClipBar.Library;

public sealed class ClipLibrary : IClipLibrary, IDisposable
{
    static readonly string[] VideoExt = [".mp4"];
    static readonly string[] ImageExt = [".png", ".jpg", ".jpeg"];
    const int ThumbWidth = 320;
    const int MaxConcurrentJobs = 2;

    readonly SettingsService _settings;
    readonly Dispatcher? _dispatcher;
    readonly SemaphoreSlim _syncGate = new(1, 1);
    readonly ConcurrentQueue<ClipInfo> _jobs = new();
    readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, int> _busyRetries = new(StringComparer.OrdinalIgnoreCase);
    int _workers;
    FileSystemWatcher? _watcher;
    string? _watchedFolder;
    readonly object _timerGate = new();
    Timer? _debounce;
    Timer? _retry;
    volatile bool _disposed;

    public ClipLibrary(SettingsService settings)
    {
        _settings = settings;
        _dispatcher = Application.Current?.Dispatcher;
        _settings.Changed += OnSettingsChanged;
        try { StartWatcher(settings.Current.ClipsFolder); }
        catch (Exception ex) { Log.Error("ClipLibrary: watcher start failed", ex); }
        _ = RefreshAsync();
    }

    public ObservableCollection<ClipInfo> Items { get; } = [];

    public string Folder => _settings.Current.ClipsFolder;

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        await _syncGate.WaitAsync();
        try
        {
            var folder = Folder;
            var snapshot = await Task.Run(() => Scan(folder));
            await OnUiAsync(() => ApplySnapshot(snapshot.Files));
            if (snapshot.BusyFiles > 0) ScheduleRetry(TimeSpan.FromSeconds(1));
            _ = Task.Run(() => PruneThumbs(snapshot.Files));
        }
        catch (Exception ex) { Log.Error("ClipLibrary.RefreshAsync failed", ex); }
        finally { _syncGate.Release(); }
    }

    public async Task<ClipInfo?> AddAsync(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            path = Path.GetFullPath(path);
            if (!IsSupported(path)) return null;
            if (!await WaitReadableAsync(path, TimeSpan.FromSeconds(20)))
            {
                Log.Warn($"ClipLibrary.AddAsync: file never became readable: {path}");
                return null;
            }
            var entry = ToEntry(new FileInfo(path));
            if (entry is null) return null;

            await _syncGate.WaitAsync();
            try
            {
                return await OnUiAsync(() =>
                {
                    var existing = Find(path);
                    if (existing is not null) return existing;
                    var clip = Create(entry.Value);
                    var idx = 0;
                    while (idx < Items.Count && Items[idx].CreatedAt > clip.CreatedAt) idx++;
                    Items.Insert(idx, clip);
                    Enqueue(clip);
                    return clip;
                });
            }
            finally { _syncGate.Release(); }
        }
        catch (Exception ex)
        {
            Log.Error($"ClipLibrary.AddAsync failed: {path}", ex);
            return null;
        }
    }

    public async Task DeleteAsync(ClipInfo clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        await _syncGate.WaitAsync();
        try
        {
            var path = clip.FilePath;
            await Task.Run(() =>
            {
                if (File.Exists(path)) ShellFileOps.RecycleFile(path);
                if (!clip.IsScreenshot) TryDelete(ThumbPathFor(path, clip.SizeBytes, clip.CreatedAt));
            });
            await OnUiAsync(() => Items.Remove(clip));
            Log.Info($"ClipLibrary: recycled {path}");
        }
        finally { _syncGate.Release(); }
    }

    public async Task<bool> RenameAsync(ClipInfo clip, string newTitle)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var title = Sanitize(newTitle);
        if (title.Length == 0) return false;

        await _syncGate.WaitAsync();
        try
        {
            var oldPath = clip.FilePath;
            var dir = Path.GetDirectoryName(oldPath)!;
            var ext = Path.GetExtension(oldPath);
            if (string.Equals(title, Path.GetFileNameWithoutExtension(oldPath), StringComparison.Ordinal)) return true;

            var newPath = await Task.Run(() =>
            {
                var target = UniquePath(dir, title, ext, oldPath);
                File.Move(oldPath, target);
                if (!clip.IsScreenshot)
                {
                    var oldThumb = ThumbPathFor(oldPath, clip.SizeBytes, clip.CreatedAt);
                    var newThumb = ThumbPathFor(target, clip.SizeBytes, clip.CreatedAt);
                    if (File.Exists(oldThumb) && !File.Exists(newThumb))
                        try { File.Move(oldThumb, newThumb); } catch { }
                }
                return target;
            });

            await OnUiAsync(() =>
            {
                var idx = Items.IndexOf(clip);
                clip.FilePath = newPath;
                if (clip.IsScreenshot) clip.ThumbnailPath = newPath;
                else
                {
                    var t = ThumbPathFor(newPath, clip.SizeBytes, clip.CreatedAt);
                    clip.ThumbnailPath = File.Exists(t) ? t : null;
                    if (clip.ThumbnailPath is null || clip.Duration is null) Enqueue(clip);
                }
                if (idx >= 0) { Items.RemoveAt(idx); Items.Insert(idx, clip); }
            });
            Log.Info($"ClipLibrary: renamed {Path.GetFileName(oldPath)} -> {Path.GetFileName(newPath)}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"ClipLibrary.RenameAsync failed: {clip.FilePath}", ex);
            return false;
        }
        finally { _syncGate.Release(); }
    }

    public void ShowInExplorer(ClipInfo clip)
    {
        try
        {
            if (File.Exists(clip.FilePath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{clip.FilePath}\"") { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(clip.FilePath)}\"") { UseShellExecute = false });
        }
        catch (Exception ex) { Log.Error("ShowInExplorer failed", ex); }
    }

    public void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = false });
        }
        catch (Exception ex) { Log.Error("OpenFolder failed", ex); }
    }

    public void Dispose()
    {
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _debounce?.Dispose();
        _retry?.Dispose();
        StopWatcher();
    }

    readonly record struct FileEntry(string Path, long Size, DateTime Modified, bool IsImage);
    readonly record struct Snapshot(List<FileEntry> Files, int BusyFiles);

    static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return VideoExt.Contains(ext, StringComparer.OrdinalIgnoreCase) || ImageExt.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    static FileEntry? ToEntry(FileInfo f)
    {
        if (!f.Exists || f.Length == 0) return null;
        var ext = f.Extension;
        var isImage = ImageExt.Contains(ext, StringComparer.OrdinalIgnoreCase);
        if (!isImage && !VideoExt.Contains(ext, StringComparer.OrdinalIgnoreCase)) return null;
        return new FileEntry(f.FullName, f.Length, f.LastWriteTime, isImage);
    }

    Snapshot Scan(string folder)
    {
        var list = new List<FileEntry>();
        var busy = 0;
        if (!Directory.Exists(folder)) return new Snapshot(list, 0);
        var recent = DateTime.Now.AddMinutes(-3);
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            FileEntry? e;
            try { e = ToEntry(f); } catch { continue; }
            if (e is null) continue;
            if (f.LastWriteTime > recent && IsLockedForWrite(f.FullName))
            {
                var n = _busyRetries.AddOrUpdate(f.FullName, 1, (_, v) => v + 1);
                if (n == 31) Log.Warn($"ClipLibrary: '{f.FullName}' still locked after 30 scans — still waiting for it to close");
                busy++;
                continue;
            }
            _busyRetries.TryRemove(f.FullName, out _);
            list.Add(e.Value);
        }
        list.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return new Snapshot(list, busy);
    }

    static bool IsLockedForWrite(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    static async Task<bool> WaitReadableAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        long lastSize = -1;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 0 && !IsLockedForWrite(path))
                {
                    if (fi.Length == lastSize) return true;
                    lastSize = fi.Length;
                }
            }
            catch { }
            await Task.Delay(250);
        }
        return File.Exists(path);
    }

    void ApplySnapshot(List<FileEntry> files)
    {
        var byPath = new Dictionary<string, ClipInfo>(Items.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var c in Items) byPath.TryAdd(c.FilePath, c);

        var desired = new List<ClipInfo>(files.Count);
        foreach (var f in files)
        {
            if (byPath.TryGetValue(f.Path, out var c))
            {
                if (c.SizeBytes != f.Size || c.CreatedAt != f.Modified)
                {
                    c.SizeBytes = f.Size; c.CreatedAt = f.Modified;
                    c.Duration = null; c.ThumbnailPath = c.IsScreenshot ? f.Path : null;
                    Enqueue(c);
                }
                desired.Add(c);
            }
            else
            {
                var n = Create(f);
                desired.Add(n);
                Enqueue(n);
            }
        }

        var keep = new HashSet<ClipInfo>(desired, ReferenceEqualityComparer.Instance);
        for (var i = Items.Count - 1; i >= 0; i--)
            if (!keep.Contains(Items[i])) Items.RemoveAt(i);

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], desired[i])) continue;
            var j = -1;
            for (var k = i + 1; k < Items.Count; k++)
                if (ReferenceEquals(Items[k], desired[i])) { j = k; break; }
            if (j >= 0) Items.Move(j, i); else Items.Insert(i, desired[i]);
        }
    }

    ClipInfo? Find(string path)
    {
        foreach (var c in Items)
            if (string.Equals(c.FilePath, path, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    static ClipInfo Create(FileEntry f) => new()
    {
        FilePath = f.Path,
        IsScreenshot = f.IsImage,
        CreatedAt = f.Modified,
        SizeBytes = f.Size,
        ThumbnailPath = f.IsImage ? f.Path : null,
    };

    void Enqueue(ClipInfo clip)
    {
        if (!_queued.TryAdd(clip.FilePath, 0)) return;
        _jobs.Enqueue(clip);
        while (true)
        {
            var n = Volatile.Read(ref _workers);
            if (n >= MaxConcurrentJobs) return;
            if (Interlocked.CompareExchange(ref _workers, n + 1, n) == n) { _ = Task.Run(WorkerLoopAsync); return; }
        }
    }

    async Task WorkerLoopAsync()
    {
        try
        {
            while (!_disposed && _jobs.TryDequeue(out var clip))
            {
                _queued.TryRemove(clip.FilePath, out _);
                try { await ProcessAsync(clip); }
                catch (Exception ex) { Log.Error($"ClipLibrary: metadata failed for {clip.FileName}", ex); }
            }
        }
        finally { Interlocked.Decrement(ref _workers); }
    }

    async Task ProcessAsync(ClipInfo clip)
    {
        var path = clip.FilePath;
        if (!File.Exists(path)) return;

        if (clip.IsScreenshot)
        {
            var (w, h) = ReadImageSize(path);
            await OnUiAsync(() => { clip.Width = w; clip.Height = h; clip.ThumbnailPath = path; });
            return;
        }

        var meta = await ProbeAsync(path);
        var thumb = ThumbPathFor(path, clip.SizeBytes, clip.CreatedAt);
        if (!File.Exists(thumb))
        {
            Directory.CreateDirectory(AppPaths.ThumbsDir);
            var seek = meta.Duration is { TotalSeconds: >= 3 } ? 1.0 : 0.0;
            var ok = await MakeThumbAsync(path, thumb, seek);
            if (!ok && seek > 0) ok = await MakeThumbAsync(path, thumb, 0);
            if (!ok) Log.Warn($"ClipLibrary: thumbnail failed for {clip.FileName}");
        }
        var thumbPath = File.Exists(thumb) ? thumb : null;
        if (!string.Equals(clip.FilePath, path, StringComparison.OrdinalIgnoreCase)) return;
        await OnUiAsync(() =>
        {
            clip.Duration = meta.Duration;
            clip.Width = meta.Width; clip.Height = meta.Height;
            clip.AudioTracks = meta.AudioTracks;
            clip.ThumbnailPath = thumbPath;
        });
    }

    readonly record struct Meta(TimeSpan? Duration, int Width, int Height, int AudioTracks);

    static async Task<Meta> ProbeAsync(string path)
    {
        try
        {
            var r = await Ffmpeg.ProbeAsync($"-v error -print_format json -show_format -show_streams {Ffmpeg.Q(path)}");
            if (!r.Ok || string.IsNullOrWhiteSpace(r.StdOut)) return default;
            using var doc = JsonDocument.Parse(r.StdOut);
            var root = doc.RootElement;
            TimeSpan? duration = null;
            if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d)
                && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
                duration = TimeSpan.FromSeconds(secs);
            int w = 0, h = 0, audio = 0;
            if (root.TryGetProperty("streams", out var streams))
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    if (type == "video" && w == 0)
                    {
                        if (s.TryGetProperty("width", out var wv)) w = wv.GetInt32();
                        if (s.TryGetProperty("height", out var hv)) h = hv.GetInt32();
                        if (duration is null && s.TryGetProperty("duration", out var sd)
                            && double.TryParse(sd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ss))
                            duration = TimeSpan.FromSeconds(ss);
                    }
                    else if (type == "audio") audio++;
                }
            return new Meta(duration, w, h, audio);
        }
        catch (Exception ex)
        {
            Log.Error($"ffprobe failed: {path}", ex);
            return default;
        }
    }

    static async Task<bool> MakeThumbAsync(string video, string thumb, double seekSeconds)
    {
        var tmp = thumb + ".part.jpg";
        var ss = seekSeconds > 0 ? $"-ss {Ffmpeg.Sec(TimeSpan.FromSeconds(seekSeconds))} " : "";
        var r = await Ffmpeg.RunAsync(
            $"-y -v error -threads 2 {ss}-i {Ffmpeg.Q(video)} -an -sn -frames:v 1 -vf \"scale={ThumbWidth}:-2\" -q:v 4 -f image2 {Ffmpeg.Q(tmp)}");
        if (r.Ok && File.Exists(tmp) && new FileInfo(tmp).Length > 0)
        {
            try { File.Move(tmp, thumb, overwrite: true); return true; }
            catch (Exception ex) { Log.Error("thumb move failed", ex); }
        }
        TryDelete(tmp);
        return false;
    }

    static (int, int) ReadImageSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var f = dec.Frames[0];
            return (f.PixelWidth, f.PixelHeight);
        }
        catch { return (0, 0); }
    }

    static string ThumbPathFor(string path, long size, DateTime modified)
    {
        var key = $"{path.ToLowerInvariant()}|{size}|{modified.Ticks}";
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(AppPaths.ThumbsDir, hash + ".jpg");
    }

    void PruneThumbs(List<FileEntry> files)
    {
        try
        {
            if (!Directory.Exists(AppPaths.ThumbsDir)) return;
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) if (!f.IsImage) live.Add(ThumbPathFor(f.Path, f.Size, f.Modified));
            var cutoff = DateTime.Now.AddDays(-1);
            foreach (var t in Directory.EnumerateFiles(AppPaths.ThumbsDir, "*.jpg"))
            {
                var name = Path.GetFileNameWithoutExtension(t);
                if (name.Length != 40 || live.Contains(t)) continue;
                if (File.GetLastWriteTime(t) < cutoff) TryDelete(t);
            }
        }
        catch (Exception ex) { Log.Warn("PruneThumbs: " + ex.Message); }
    }

    void StartWatcher(string folder)
    {
        StopWatcher();
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var w = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024,
        };
        w.Created += OnFsEvent;
        w.Deleted += OnFsEvent;
        w.Changed += OnFsEvent;
        w.Renamed += OnFsEvent;
        w.Error += (_, e) => { Log.Warn("ClipLibrary watcher error: " + e.GetException().Message); ScheduleRefresh(); };
        w.EnableRaisingEvents = true;
        _watcher = w;
        _watchedFolder = folder;
    }

    void StopWatcher()
    {
        var w = _watcher;
        _watcher = null;
        if (w is null) return;
        try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
    }

    void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        var relevant = IsSupported(e.FullPath) || (e is RenamedEventArgs r && IsSupported(r.OldFullPath));
        if (relevant) ScheduleRefresh();
    }

    void ScheduleRefresh()
    {
        if (_disposed) return;
        lock (_timerGate)
        {
            _debounce ??= new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(400, Timeout.Infinite);
        }
    }

    void ScheduleRetry(TimeSpan delay)
    {
        if (_disposed) return;
        lock (_timerGate)
        {
            _retry ??= new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _retry.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    void OnSettingsChanged(object? sender, AppSettings s)
    {
        if (string.Equals(s.ClipsFolder, _watchedFolder, StringComparison.OrdinalIgnoreCase)) return;
        try { StartWatcher(s.ClipsFolder); }
        catch (Exception ex) { Log.Error("ClipLibrary: watcher re-target failed", ex); }
        _ = RefreshAsync();
    }

    static string Sanitize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(title.Length);
        foreach (var ch in title) sb.Append(Array.IndexOf(invalid, ch) >= 0 ? ' ' : ch);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        if (s.Length > 120) s = s[..120].TrimEnd();
        if (s.Length is >= 3 and <= 4 && (s.Equals("CON", StringComparison.OrdinalIgnoreCase) || s.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || s.Equals("AUX", StringComparison.OrdinalIgnoreCase) || s.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || ((s.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || s.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && s.Length == 4 && char.IsDigit(s[3]))))
            s += "_";
        return s;
    }

    static string UniquePath(string dir, string title, string ext, string self)
    {
        var candidate = Path.Combine(dir, title + ext);
        if (!File.Exists(candidate) || string.Equals(candidate, self, StringComparison.OrdinalIgnoreCase)) return candidate;
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(dir, $"{title} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    Task OnUiAsync(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess() || _dispatcher.HasShutdownStarted) { action(); return Task.CompletedTask; }
        return _dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
    }

    Task<T> OnUiAsync<T>(Func<T> func)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess() || _dispatcher.HasShutdownStarted) return Task.FromResult(func());
        return _dispatcher.InvokeAsync(func, DispatcherPriority.Background).Task;
    }
}

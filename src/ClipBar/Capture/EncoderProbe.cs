using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClipBar.Core;

namespace ClipBar.Capture;

internal sealed record WorkingEncoder(string Id, int Variant)
{
    public EncoderSpec Spec => EncoderSpec.Find(Id)!;
}

internal sealed class EncoderProbe(SettingsService settings)
{
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(25);
    const double ProbeSeconds = 0.7;

    readonly SemaphoreSlim _gate = new(1, 1);
    IReadOnlyList<WorkingEncoder>? _working;

    sealed class CacheFile
    {
        public string Key { get; set; } = "";
        public DateTime ProbedAt { get; set; }
        public List<WorkingEncoder> Working { get; set; } = [];
    }

    static string CachePath => Path.Combine(AppPaths.DataDir, "encoders.json");

    public async Task<IReadOnlyList<WorkingEncoder>> GetWorkingAsync(CancellationToken ct = default)
    {
        if (_working is not null) return _working;
        await _gate.WaitAsync(ct);
        try
        {
            if (_working is not null) return _working;
            var key = CacheKey(settings.Current);
            var cached = LoadCache(key);
            if (cached is not null) { _working = cached; return cached; }

            var sw = Stopwatch.StartNew();
            var list = new List<WorkingEncoder>();
            var nativeH = SafeNativeHeight(settings.Current.MonitorIndex);
            foreach (var spec in EncoderSpec.All)
            {
                ct.ThrowIfCancellationRequested();
                for (var variant = 0; variant < spec.Variants; variant++)
                {
                    var (ok, why) = await ProbeOneAsync(spec, variant, nativeH, ct);
                    Log.Info($"Encoder probe {spec.Id} v{variant}: {(ok ? "OK" : "no")} {why}");
                    if (ok) { list.Add(new WorkingEncoder(spec.Id, variant)); break; }
                }
            }
            Log.Info($"Encoder probe finished in {sw.Elapsed.TotalSeconds:0.0}s: {string.Join(", ", list.Select(w => w.Id))}");
            _working = list;
            SaveCache(key, list);
            return list;
        }
        finally { _gate.Release(); }
    }

    public void Invalidate()
    {
        _working = null;
        try { File.Delete(CachePath); } catch { }
    }

    public async Task<IReadOnlyList<EncoderInfo>> GetInfosAsync(CancellationToken ct = default)
    {
        var working = await GetWorkingAsync(ct);
        var result = new List<EncoderInfo>(working.Count);
        var recommended = true;
        foreach (var w in working)
        {
            var spec = w.Spec;
            result.Add(new EncoderInfo(spec.Id, spec.DisplayName(recommended && spec.Hardware), spec.Hardware));
            if (spec.Hardware) recommended = false;
        }
        return result;
    }

    async Task<(bool ok, string why)> ProbeOneAsync(EncoderSpec spec, int variant, int nativeH, CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.TempDir);
        var outPath = Path.Combine(AppPaths.TempDir, $"probe_{spec.Id}_{variant}.ts");
        try
        {
            var args = CaptureCommand.BuildProbe(settings.Current, spec, variant, nativeH, ProbeSeconds, outPath);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            FfResult r;
            try { r = await Ffmpeg.RunAsync(args, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (false, "timeout"); }
            if (!r.Ok) return (false, LastLine(r.StdErr));
            if (!File.Exists(outPath) || new FileInfo(outPath).Length < 1024) return (false, "empty output");

            var p = await Ffmpeg.ProbeAsync($"-v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 {Ffmpeg.Q(outPath)}", ct);
            var codec = p.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().TrimEnd(',');
            return codec == spec.Codec ? (true, "") : (false, $"container check failed (codec={codec ?? "none"})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { File.Delete(outPath); } catch { }
        }
    }

    static string LastLine(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var l = lines.LastOrDefault(x => x.Contains("rror", StringComparison.Ordinal) || x.Contains("failed", StringComparison.OrdinalIgnoreCase)) ?? lines.LastOrDefault() ?? "";
        return l.Length > 120 ? l[..120] : l;
    }

    static int SafeNativeHeight(int monitorIndex)
    {
        try { return ScreenGrabber.GetMonitor(monitorIndex).Height; }
        catch { try { return ScreenGrabber.GetMonitor(0).Height; } catch { return 0; } }
    }

    static string CacheKey(AppSettings s)
    {
        string ff = AppPaths.FfmpegPath, size = "?";
        try { size = new FileInfo(ff).Length.ToString(); } catch { }
        return $"{ff}|{size}|{string.Join("+", GpuNames())}|mon={s.MonitorIndex}|fps={s.Fps}|h={s.OutputHeight}|cursor={s.CaptureCursor}";
    }

    static IReadOnlyList<WorkingEncoder>? LoadCache(string key)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath));
            if (c is null || c.Key != key) return null;
            var list = c.Working.Where(w => EncoderSpec.Find(w.Id) is not null).ToList();
            return list.Count == 0 && c.Working.Count > 0 ? null : list;
        }
        catch (Exception ex) { Log.Warn("encoders.json unreadable: " + ex.Message); return null; }
    }

    static void SaveCache(string key, List<WorkingEncoder> list)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var json = JsonSerializer.Serialize(new CacheFile { Key = key, ProbedAt = DateTime.UtcNow, Working = list },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CachePath, json);
        }
        catch (Exception ex) { Log.Warn("encoders.json write failed: " + ex.Message); }
    }

    public static List<string> GpuNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if (!string.IsNullOrWhiteSpace(dd.DeviceString)) names.Add(dd.DeviceString.Trim());
                dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            }
        }
        catch { }
        return [.. names];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);
}

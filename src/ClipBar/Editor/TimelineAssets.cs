using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ClipBar.Core;

namespace ClipBar.Editor;

public static class TimelineAssets
{
    static string CacheDir => Path.Combine(AppPaths.TempDir, "editor");
    static bool _cleaned;

    public static async Task<string?> GetFilmstripAsync(string path, TimeSpan duration, int frames, int height, CancellationToken ct = default)
    {
        frames = Math.Clamp(frames, 2, 120);
        var seconds = Math.Max(0.2, duration.TotalSeconds);
        var output = CachePath(path, $"strip_{frames}x{height}.jpg");
        if (File.Exists(output)) return output;

        var fps = (frames / seconds).ToString("0.######", CultureInfo.InvariantCulture);
        var vf = $"fps={fps}:round=up,scale=-2:{height},tile={frames}x1:color=0x1A1A1A";
        var args = $"-hide_banner -loglevel error -y -nostdin -skip_frame nokey -i {Ffmpeg.Q(path)} " +
                   $"-vf \"{vf}\" -frames:v 1 -update 1 -q:v 4 {Ffmpeg.Q(output)}";
        return await RunAsync(args, output, ct);
    }

    public static async Task<string?> GetWaveformAsync(string path, int width, int height, CancellationToken ct = default)
    {
        width = Math.Clamp(width, 64, 8192);
        var output = CachePath(path, $"wave_{width}x{height}.png");
        if (File.Exists(output)) return output;

        var fc = $"[0:a:0]aformat=channel_layouts=mono,showwavespic=s={width}x{height}:colors=#4CC2FF:scale=sqrt:draw=full[w]";
        var args = $"-hide_banner -loglevel error -y -nostdin -i {Ffmpeg.Q(path)} -filter_complex \"{fc}\" " +
                   $"-map \"[w]\" -frames:v 1 -update 1 {Ffmpeg.Q(output)}";
        return await RunAsync(args, output, ct);
    }

    static async Task<string?> RunAsync(string args, string output, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            CleanupOnce();
            var r = await Ffmpeg.RunAsync(args, ct);
            if (r.Ok && File.Exists(output) && new FileInfo(output).Length > 0) return output;
            Log.Warn($"Timeline asset failed ({r.ExitCode}): {LastLine(r.StdErr)}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"Timeline asset failed: {ex.Message}"); }
        try { if (File.Exists(output)) File.Delete(output); } catch { }
        return null;
    }

    static string CachePath(string path, string suffix)
    {
        long size = 0, ticks = 0;
        try { var fi = new FileInfo(path); size = fi.Length; ticks = fi.LastWriteTimeUtc.Ticks; } catch { }
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{path.ToLowerInvariant()}|{size}|{ticks}")))[..16];
        return Path.Combine(CacheDir, $"{key}_{suffix}");
    }

    static void CleanupOnce()
    {
        if (_cleaned) return;
        _cleaned = true;
        try
        {
            var limit = DateTime.UtcNow.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(CacheDir))
                if (File.GetLastWriteTimeUtc(f) < limit) File.Delete(f);
        }
        catch { }
    }

    static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : "";
    }
}

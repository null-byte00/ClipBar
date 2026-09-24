using System.Globalization;
using System.IO;
using ClipBar.Core;

namespace ClipBar.Editor;

public sealed class ExportException(string message) : Exception(message);

public sealed record ExportProgress(double Fraction, string Status);

public sealed class ExportService
{
    public const long DiscordLimitBytes = 10L * 1024 * 1024;

    static readonly string[] EncoderPreference = ["h264_qsv", "h264_nvenc", "h264_amf", "libx264"];

    public async Task<string> ExportAsync(ExportRequest req, IProgress<ExportProgress>? progress = null, CancellationToken ct = default)
    {
        Validate(req);
        var preset = ExportPreset.Get(req.Preset);
        var folder = req.OutputFolder ?? AppServices.Settings?.Current.ClipsFolder ?? Path.GetDirectoryName(req.SourcePath)!;
        Directory.CreateDirectory(folder);
        var output = UniquePath(folder, SanitizeTitle(req.Title), preset.Extension);

        progress?.Report(new(0, "Подготовка…"));
        Log.Info($"Export {req.Preset}: {req.SourcePath} [{Ffmpeg.Sec(req.In)}..{Ffmpeg.Sec(req.Out)}] -> {output}");
        try
        {
            switch (req.Preset)
            {
                case ExportPresetKind.FastCopy: await FastCopyAsync(req, output, progress, ct); break;
                case ExportPresetKind.Precise: await ReencodeAsync(req, output, ReencodeMode.Precise, progress, ct); break;
                case ExportPresetKind.Vertical: await ReencodeAsync(req, output, ReencodeMode.Vertical, progress, ct); break;
                case ExportPresetKind.Discord: await DiscordAsync(req, output, progress, ct); break;
                case ExportPresetKind.Gif: await GifAsync(req, output, progress, ct); break;
                case ExportPresetKind.Mp3: await Mp3Async(req, output, progress, ct); break;
                default: throw new ExportException("Неизвестный пресет");
            }
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new ExportException("ffmpeg не создал файл");
        }
        catch
        {
            TryDelete(output);
            throw;
        }

        progress?.Report(new(1, "Готово"));
        Log.Info($"Export done: {output} ({new FileInfo(output).Length} bytes)");

        try { if (AppServices.Library is { } lib) await lib.AddAsync(output); }
        catch (Exception ex) { Log.Warn($"Library.AddAsync failed for {output}: {ex.Message}"); }
        try { AppServices.Notifier?.Show("Экспорт завершён", Path.GetFileName(output), NotifyKind.Success, preset.IsVideo ? output : null); }
        catch (Exception ex) { Log.Warn($"Notifier failed: {ex.Message}"); }
        return output;
    }

    async Task FastCopyAsync(ExportRequest req, string output, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var audio = !req.Info.HasAudio ? "-an"
            : req.AudioChanged ? $"-filter_complex \"{MixFilter(req)}\" -map \"[a]\" -c:a aac -b:a 192k"
            : "-map 0:a -c:a copy";
        var tag = req.Info.VideoCodec is "hevc" or "h265" ? " -tag:v hvc1" : "";
        var args = $"{Seek(req)} -i {Ffmpeg.Q(req.SourcePath)} -map 0:v:0 {audio} -c:v copy{tag} " +
                   $"-avoid_negative_ts make_zero -movflags +faststart {Ffmpeg.Q(output)}";
        await RunAsync(args, req.Length, progress, "Копирование без перекодирования…", ct);
    }

    enum ReencodeMode { Precise, Vertical, Discord }

    sealed record VideoPlan(int Width, int Height, int CropW, int CropH, int CropX, int CropY, int Kbps, int Fps, bool Scale, bool Crop);

    async Task ReencodeAsync(ExportRequest req, string output, ReencodeMode mode, IProgress<ExportProgress>? progress, CancellationToken ct,
        VideoPlan? plan = null, string? status = null)
    {
        plan ??= PlanVideo(req, mode);
        var encoder = await PickEncoderAsync();
        var audio = AudioArgs(req, mode == ReencodeMode.Discord ? 128 : 192);
        status ??= encoder == "libx264" ? "Перекодирование (процессор)…" : "Перекодирование на видеокарте…";

        if (encoder == "h264_qsv")
        {
            var vpp = new List<string>();
            if (plan.Crop) vpp.Add($"cw={plan.CropW}:ch={plan.CropH}:cx={plan.CropX}:cy={plan.CropY}");
            if (plan.Crop || plan.Scale) vpp.Add($"w={plan.Width}:h={plan.Height}");
            vpp.Add("format=nv12");
            var gpuArgs = $"-hwaccel qsv -hwaccel_output_format qsv {Seek(req)} -i {Ffmpeg.Q(req.SourcePath)} -map 0:v:0 {audio} " +
                          $"-vf \"vpp_qsv={string.Join(':', vpp)}\" {FpsArg(req, plan)}{EncoderArgs(encoder, plan, req)} -movflags +faststart {Ffmpeg.Q(output)}";
            try
            {
                await RunAsync(gpuArgs, req.Length, progress, status, ct);
                return;
            }
            catch (ExportException ex)
            {
                Log.Warn($"GPU decode path failed, falling back to software decode: {ex.Message}");
                TryDelete(output);
            }
        }

        var vf = new List<string>();
        if (plan.Crop) vf.Add($"crop={plan.CropW}:{plan.CropH}:{plan.CropX}:{plan.CropY}");
        if (plan.Crop || plan.Scale) vf.Add($"scale={plan.Width}:{plan.Height}:flags=bicubic");
        vf.Add(encoder == "libx264" ? "format=yuv420p" : "format=nv12");
        var swArgs = $"{Seek(req)} -i {Ffmpeg.Q(req.SourcePath)} -map 0:v:0 {audio} -vf \"{string.Join(',', vf)}\" " +
                     $"{FpsArg(req, plan)}{EncoderArgs(encoder, plan, req)} -movflags +faststart {Ffmpeg.Q(output)}";
        await RunAsync(swArgs, req.Length, progress, status, ct);
    }

    async Task DiscordAsync(ExportRequest req, string output, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var plan = PlanVideo(req, ReencodeMode.Discord);
        long size = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var status = attempt == 0 ? "Сжатие под лимит 10 МБ…" : $"Повторное сжатие ({attempt + 1}/3)…";
            await ReencodeAsync(req, output, ReencodeMode.Discord, progress, ct, plan, status);
            size = new FileInfo(output).Length;
            if (size <= DiscordLimitBytes) return;
            var factor = (double)DiscordLimitBytes * 0.93 / size;
            var kbps = Math.Max(150, (int)(plan.Kbps * factor));
            Log.Warn($"Discord export overshoot: {size} bytes at {plan.Kbps} kbit/s, retrying at {kbps}");
            plan = plan with { Kbps = kbps };
            TryDelete(output);
        }
        throw new ExportException($"Не удалось уложиться в 10 МБ ({size / 1024.0 / 1024.0:0.0} МБ). Укороти фрагмент.");
    }

    async Task GifAsync(ExportRequest req, string output, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        const string vf = "fps=15,scale='min(480,iw)':-2:flags=lanczos,split[a][b];" +
                          "[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle";
        var args = $"{Seek(req)} -i {Ffmpeg.Q(req.SourcePath)} -map 0:v:0 -an -vf \"{vf}\" -loop 0 {Ffmpeg.Q(output)}";
        await RunAsync(args, req.Length, progress, "Создание GIF…", ct);
    }

    async Task Mp3Async(ExportRequest req, string output, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        if (!req.Info.HasAudio) throw new ExportException("В клипе нет звуковой дорожки");
        var map = req.AudioChanged ? $"-filter_complex \"{MixFilter(req)}\" -map \"[a]\"" : "-map 0:a:0";
        var args = $"{Seek(req)} -i {Ffmpeg.Q(req.SourcePath)} -vn {map} -c:a libmp3lame -b:a 192k -id3v2_version 3 {Ffmpeg.Q(output)}";
        await RunAsync(args, req.Length, progress, "Кодирование MP3…", ct);
    }

    static VideoPlan PlanVideo(ExportRequest req, ReencodeMode mode)
    {
        var info = req.Info;
        int w = info.Width, h = info.Height;
        var fps = info.Fps > 1 ? (int)Math.Round(info.Fps) : 60;
        var srcKbps = info.VideoBitrate > 0 ? (int)(info.VideoBitrate / 1000)
            : AppServices.Settings?.Current.VideoBitrateKbps ?? 12000;

        switch (mode)
        {
            case ReencodeMode.Precise:
                return new VideoPlan(w, h, w, h, 0, 0, Math.Clamp(srcKbps, 1500, 60000), fps, false, false);

            case ReencodeMode.Vertical:
            {
                int cw, ch;
                if (w * 16 >= h * 9) { ch = h; cw = Even(h * 9.0 / 16); }
                else { cw = w; ch = Even(w * 16.0 / 9); }
                var cx = Even((w - cw) / 2.0);
                var cy = Even((h - ch) / 2.0);
                return new VideoPlan(1080, 1920, cw, ch, cx, cy, Math.Clamp(srcKbps, 4000, 30000), fps, true, true);
            }

            default:
            {
                const int audioKbps = 128;
                var seconds = Math.Max(0.1, req.Length.TotalSeconds);
                var totalKbps = DiscordLimitBytes * 8.0 / 1000 * 0.94 / seconds;
                var kbps = (int)(totalKbps - (info.HasAudio ? audioKbps : 0) - 8);
                kbps = Math.Clamp(kbps, 150, 12000);

                int tw = w, th = h;
                var targetFps = fps;
                if (h > 720 && kbps < 10000) { th = 720; tw = Even(w * 720.0 / h); }
                if (kbps < 1500 && fps > 30) targetFps = 30;
                if (kbps < 700 && th > 480) { th = 480; tw = Even(w * 480.0 / h); }
                return new VideoPlan(tw, th, w, h, 0, 0, kbps, targetFps, tw != w || th != h, false);
            }
        }
    }

    static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    static string FpsArg(ExportRequest req, VideoPlan plan) =>
        req.Info.Fps > plan.Fps + 0.5 ? $"-r {plan.Fps} " : "";

    static string EncoderArgs(string encoder, VideoPlan plan, ExportRequest req)
    {
        var gop = plan.Fps;
        var rate = $"-b:v {plan.Kbps}k -maxrate {(int)(plan.Kbps * 1.1)}k -bufsize {plan.Kbps * 2}k -g {gop}";
        return encoder switch
        {
            "h264_qsv" => $"-c:v h264_qsv -preset veryfast -bf 0 {rate}",
            "h264_nvenc" => $"-c:v h264_nvenc -preset p4 -rc vbr -bf 0 {rate}",
            "h264_amf" => $"-c:v h264_amf -usage transcoding -quality balanced -rc vbr_peak -bf 0 {rate}",
            _ => $"-c:v libx264 -preset veryfast {rate}",
        };
    }

    static async Task<string> PickEncoderAsync()
    {
        try
        {
            if (AppServices.Engine is { } engine)
            {
                var ids = (await engine.GetAvailableEncodersAsync()).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var pref in EncoderPreference)
                    if (ids.Contains(pref)) return pref;
            }
        }
        catch (Exception ex) { Log.Warn($"GetAvailableEncodersAsync failed: {ex.Message}"); }
        return "libx264";
    }

    static string MixFilter(ExportRequest req) => req.Info.HasSeparateTracks
        ? $"[0:a:1]volume={G(req.SystemGain)}[s];[0:a:2]volume={G(req.MicGain)}[m];[s][m]amix=inputs=2:normalize=0,{Limiter}[a]"
        : $"[0:a:0]volume={G(req.MasterGain)},{Limiter}[a]";

    const string Limiter = "alimiter=limit=0.95:level=false:attack=5:release=50";

    static string AudioArgs(ExportRequest req, int kbps)
    {
        if (!req.Info.HasAudio) return "-an";
        if (req.AudioChanged) return $"-filter_complex \"{MixFilter(req)}\" -map \"[a]\" -c:a aac -b:a {kbps}k";
        return req.Info.HasSeparateTracks ? $"-map 0:a -c:a aac -b:a {kbps}k" : $"-map 0:a:0 -c:a aac -b:a {kbps}k";
    }

    static string G(float gain) => Math.Clamp(gain, 0f, 4f).ToString("0.###", CultureInfo.InvariantCulture);

    static string Seek(ExportRequest req) => $"-ss {Ffmpeg.Sec(req.In)} -to {Ffmpeg.Sec(req.Out)}";

    static async Task RunAsync(string args, TimeSpan total, IProgress<ExportProgress>? progress, string status, CancellationToken ct)
    {
        progress?.Report(new(0, status));
        var full = $"-hide_banner -loglevel error -y -nostdin -progress pipe:1 -nostats {args}";
        Log.Info("ffmpeg " + full);
        var r = await Ffmpeg.RunAsync(full, ct, t =>
        {
            var f = total > TimeSpan.Zero ? Math.Clamp(t / total, 0, 0.995) : 0;
            progress?.Report(new(f, status));
        });
        ct.ThrowIfCancellationRequested();
        if (!r.Ok) throw new ExportException(Friendly(r.StdErr, r.ExitCode));
    }

    static string Friendly(string stderr, int code)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("Last message repeated", StringComparison.Ordinal)).ToArray();
        var last = lines.Length > 0 ? lines[^1] : $"ffmpeg завершился с кодом {code}";
        if (last.Contains("No space left", StringComparison.OrdinalIgnoreCase)) return "Недостаточно места на диске";
        if (last.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return "Нет доступа к папке для сохранения";
        return last.Length > 200 ? last[..200] + "…" : last;
    }

    static void Validate(ExportRequest req)
    {
        if (req.Out - req.In < TimeSpan.FromMilliseconds(100)) throw new ExportException("Слишком короткий фрагмент");
        if (req.In < TimeSpan.Zero || req.Out > req.Info.Duration + TimeSpan.FromSeconds(1)) throw new ExportException("Границы вне клипа");
        if (req.Preset != ExportPresetKind.Mp3 && !req.Info.HasVideo) throw new ExportException("В файле нет видео");
    }

    public static string SanitizeTitle(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = title.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim(' ', '.');
        return s.Length == 0 ? "Клип" : s.Length > 120 ? s[..120] : s;
    }

    public static string UniquePath(string folder, string title, string ext)
    {
        var path = Path.Combine(folder, title + ext);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{title} ({i}){ext}");
        return path;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

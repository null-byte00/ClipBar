using System.Globalization;
using System.IO;
using System.Text;
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

        var kind = req.Preset;
        if (kind == ExportPresetKind.FastCopy && req.VideoChanged) kind = ExportPresetKind.Precise;

        string? textFile = null;
        if (req.HasText)
        {
            textFile = PrepareTextFile(req.Text!);
            req.TextFilter = BuildDrawText(req, textFile);
        }

        progress?.Report(new(0, "Подготовка…"));
        Log.Info($"Export {kind}: {req.SourcePath} [{Ffmpeg.Sec(req.In)}..{Ffmpeg.Sec(req.Out)}]" +
                 $"{(req.SpeedChanged ? $" speed={req.Speed:0.###}x" : "")} -> {output}");
        try
        {
            if (req.AnyMulti && kind == ExportPresetKind.Gif)
                throw new ExportException("GIF не поддерживает склейку кусков. Экспортируй один фрагмент или выбери видео-пресет.");

            if (req.MultiMedia && kind == ExportPresetKind.Mp3)
            {
                await MultiMediaAudioAsync(req, output, progress, ct);
            }
            else if (req.MultiMedia && kind == ExportPresetKind.Discord)
            {
                await MultiMediaDiscordAsync(req, output, progress, ct);
            }
            else if (req.MultiMedia)
            {
                var m = kind == ExportPresetKind.Vertical ? ReencodeMode.Vertical : ReencodeMode.Precise;
                await MultiMediaAsync(req, output, m, progress, ct);
            }
            else if (req.MultiSegment && kind == ExportPresetKind.Mp3)
            {
                await MultiAudioAsync(req, output, progress, ct);
            }
            else if (req.MultiSegment && kind == ExportPresetKind.Discord)
            {
                await MultiDiscordAsync(req, output, progress, ct);
            }
            else if (req.MultiSegment)
            {
                var m = kind == ExportPresetKind.Vertical ? ReencodeMode.Vertical : ReencodeMode.Precise;
                await MultiCutAsync(req, output, m, progress, ct);
            }
            else
            {
                switch (kind)
                {
                    case ExportPresetKind.FastCopy: await FastCopyAsync(req, output, progress, ct); break;
                    case ExportPresetKind.Precise: await ReencodeAsync(req, output, ReencodeMode.Precise, progress, ct); break;
                    case ExportPresetKind.Vertical: await ReencodeAsync(req, output, ReencodeMode.Vertical, progress, ct); break;
                    case ExportPresetKind.Discord: await DiscordAsync(req, output, progress, ct); break;
                    case ExportPresetKind.Gif: await GifAsync(req, output, progress, ct); break;
                    case ExportPresetKind.Mp3: await Mp3Async(req, output, progress, ct); break;
                    default: throw new ExportException("Неизвестный пресет");
                }
            }
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new ExportException("ffmpeg не создал файл");
        }
        catch
        {
            TryDelete(output);
            throw;
        }
        finally
        {
            if (textFile is not null) TryDelete(textFile);
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
            : NeedsAudioFilter(req) ? $"-filter_complex \"{MixFilter(req)}\" -map \"[a]\" -c:a aac -b:a 192k"
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

        if (encoder == "h264_qsv" && !req.NeedsSoftwareFilters)
        {
            var vpp = new List<string>();
            if (plan.Crop) vpp.Add($"cw={plan.CropW}:ch={plan.CropH}:cx={plan.CropX}:cy={plan.CropY}");
            if (plan.Crop || plan.Scale) vpp.Add($"w={plan.Width}:h={plan.Height}");
            vpp.Add("format=nv12");
            var gpuVf = $"vpp_qsv={string.Join(':', vpp)}";
            if (req.SpeedChanged) gpuVf = $"{Setpts(req)},{gpuVf}";
            var gpuArgs = $"-hwaccel qsv -hwaccel_output_format qsv {Seek(req)} -i {Ffmpeg.Q(req.SourcePath)} -map 0:v:0 {audio} " +
                          $"-vf \"{gpuVf}\" {FpsArg(req, plan)}{EncoderArgs(encoder, plan, req)} -movflags +faststart {Ffmpeg.Q(output)}";
            try
            {
                await RunAsync(gpuArgs, req.OutputLength, progress, status, ct);
                return;
            }
            catch (ExportException ex)
            {
                Log.Warn($"GPU decode path failed, falling back to software decode: {ex.Message}");
                TryDelete(output);
            }
        }

        var vf = new List<string>();
        if (req.SpeedChanged) vf.Add(Setpts(req));
        foreach (var f in RotateFilters(req)) vf.Add(f);
        if (plan.Crop) vf.Add($"crop={plan.CropW}:{plan.CropH}:{plan.CropX}:{plan.CropY}");
        if (plan.Crop || plan.Scale) vf.Add($"scale={plan.Width}:{plan.Height}:flags=bicubic");
        if (req.ColorChanged)
            vf.Add($"eq=brightness={N(Math.Clamp(req.Brightness, -1, 1))}:" +
                   $"contrast={N(Math.Clamp(req.Contrast, 0, 3))}:saturation={N(Math.Clamp(req.Saturation, 0, 3))}");
        if (req.Mirror) vf.Add("hflip");
        if (req.TextFilter is { } dt) vf.Add(dt);
        foreach (var f in VideoFades(req)) vf.Add(f);
        vf.Add(encoder == "libx264" ? "format=yuv420p" : "format=nv12");
        var swArgs = $"{Seek(req)} -i {Ffmpeg.Q(req.SourcePath)} -map 0:v:0 {audio} -vf \"{string.Join(',', vf)}\" " +
                     $"{FpsArg(req, plan)}{EncoderArgs(encoder, plan, req)} -movflags +faststart {Ffmpeg.Q(output)}";
        await RunAsync(swArgs, req.OutputLength, progress, status, ct);
    }

    async Task MultiCutAsync(ExportRequest req, string output, ReencodeMode mode, IProgress<ExportProgress>? progress, CancellationToken ct,
        VideoPlan? plan = null, string? status = null)
    {
        var segs = req.EffectiveSegments;
        foreach (var (a, b) in segs)
        {
            if (b <= a) throw new ExportException("Некорректный кусок (конец раньше начала)");
            if (b - a < TimeSpan.FromMilliseconds(50)) throw new ExportException("Один из кусков слишком короткий");
        }

        plan ??= PlanVideo(req, mode);
        var encoder = await PickEncoderAsync();
        var hasAudio = req.Info.HasAudio;

        var sb = new StringBuilder();
        var concatIns = new StringBuilder();
        for (var i = 0; i < segs.Count; i++)
        {
            var (a, b) = segs[i];
            sb.Append($"[0:v]trim=start={N(a.TotalSeconds)}:end={N(b.TotalSeconds)},setpts=PTS-STARTPTS[v{i}];");
            concatIns.Append($"[v{i}]");
            if (hasAudio)
            {
                sb.Append($"[0:a:0]atrim=start={N(a.TotalSeconds)}:end={N(b.TotalSeconds)},asetpts=PTS-STARTPTS[a{i}];");
                concatIns.Append($"[a{i}]");
            }
        }

        var effT = req.EffectiveTransitionSeconds;

        if (effT > 0)
        {
            var type = SafeTransition(req.TransitionType);
            var cum = (segs[0].Out - segs[0].In).TotalSeconds;
            var cur = "[v0]";
            for (var i = 1; i < segs.Count; i++)
            {
                var outl = i == segs.Count - 1 ? "[vc]" : $"[vt{i}]";
                sb.Append($"{cur}[v{i}]xfade=transition={type}:duration={N(effT)}:offset={N(cum - effT)}{outl};");
                cur = outl;
                cum += (segs[i].Out - segs[i].In).TotalSeconds - effT;
            }
            if (hasAudio)
            {
                var acur = "[a0]";
                for (var i = 1; i < segs.Count; i++)
                {
                    var outl = i == segs.Count - 1 ? "[ac]" : $"[at{i}]";
                    sb.Append($"{acur}[a{i}]acrossfade=d={N(effT)}{outl};");
                    acur = outl;
                }
            }
        }
        else
        {
            sb.Append($"{concatIns}concat=n={segs.Count}:v=1:a={(hasAudio ? 1 : 0)}[vc]{(hasAudio ? "[ac]" : "")};");
        }

        var map = AppendMergedEffects(sb, req, plan, encoder, hasAudio, mode == ReencodeMode.Discord ? 128 : 192);

        status ??= $"Склейка {segs.Count} кусков…";
        var args = $"-i {Ffmpeg.Q(req.SourcePath)} -filter_complex \"{sb}\" {map} " +
                   $"{EncoderArgs(encoder, plan, req)} -movflags +faststart {Ffmpeg.Q(output)}";
        await RunAsync(args, req.OutputLength, progress, status, ct);
    }

    static string AudioTailFilters(ExportRequest req)
    {
        var afx = new List<string>();
        if (req.SpeedChanged) afx.Add(AtempoChain(req.Speed));
        if (!Near(req.MasterGain, 1f)) afx.Add($"volume={G(req.MasterGain)}");
        if (req.FadeIn > TimeSpan.Zero)
            afx.Add($"afade=t=in:st=0:d={N(Math.Min(req.FadeIn.TotalSeconds, req.OutputLength.TotalSeconds))}");
        if (req.FadeOut > TimeSpan.Zero)
        {
            var d = Math.Min(req.FadeOut.TotalSeconds, req.OutputLength.TotalSeconds);
            afx.Add($"afade=t=out:st={N(Math.Max(0, req.OutputLength.TotalSeconds - d))}:d={N(d)}");
        }
        afx.Add(Limiter);
        return string.Join(',', afx);
    }

    string AppendMergedEffects(StringBuilder sb, ExportRequest req, VideoPlan plan, string encoder, bool hasAudio, int audioKbps)
    {
        var vfx = new List<string>();
        if (req.SpeedChanged) vfx.Add(Setpts(req));
        foreach (var f in RotateFilters(req)) vfx.Add(f);
        if (plan.Crop) vfx.Add($"crop={plan.CropW}:{plan.CropH}:{plan.CropX}:{plan.CropY}");
        if (plan.Crop || plan.Scale) vfx.Add($"scale={plan.Width}:{plan.Height}:flags=bicubic");
        if (req.ColorChanged)
            vfx.Add($"eq=brightness={N(Math.Clamp(req.Brightness, -1, 1))}:" +
                    $"contrast={N(Math.Clamp(req.Contrast, 0, 3))}:saturation={N(Math.Clamp(req.Saturation, 0, 3))}");
        if (req.Mirror) vfx.Add("hflip");
        if (req.TextFilter is { } dt) vfx.Add(dt);
        foreach (var f in VideoFades(req)) vfx.Add(f);

        if (req.Info.Fps > plan.Fps + 0.5) vfx.Add($"fps={plan.Fps}");
        vfx.Add(encoder == "libx264" ? "format=yuv420p" : "format=nv12");
        sb.Append($"[vc]{string.Join(',', vfx)}[v]");

        var map = "-map \"[v]\"";
        if (hasAudio)
        {
            sb.Append($";[ac]{AudioTailFilters(req)}[a]");
            map += $" -map \"[a]\" -c:a aac -b:a {audioKbps}k";
        }
        else map += " -an";
        return map;
    }

    async Task MultiMediaAsync(ExportRequest req, string output, ReencodeMode mode, IProgress<ExportProgress>? progress, CancellationToken ct,
        VideoPlan? plan = null, string? status = null)
    {
        var segs = req.MediaSegments!;
        foreach (var s in segs)
        {
            if (s.Out <= s.In) throw new ExportException("Некорректный кусок (конец раньше начала)");
            if (s.Out - s.In < TimeSpan.FromMilliseconds(50)) throw new ExportException("Один из кусков слишком короткий");
        }

        plan ??= PlanVideo(req, mode);
        var encoder = await PickEncoderAsync();
        var hasAnyAudio = segs.Any(s => s.HasAudio);

        var tw = req.Info.Width > 0 ? req.Info.Width : 1280;
        var th = req.Info.Height > 0 ? req.Info.Height : 720;
        var fps = req.Info.Fps > 1 ? (int)Math.Round(req.Info.Fps) : 60;
        var norm = $"scale={tw}:{th}:force_original_aspect_ratio=decrease," +
                   $"pad={tw}:{th}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={fps},format=yuv420p";

        var inputList = new List<string>();
        var sb = new StringBuilder();
        var concatIns = new StringBuilder();
        for (var i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            var idx = inputList.Count;
            inputList.Add($"-i {Ffmpeg.Q(s.Path)}");
            var len = (s.Out - s.In).TotalSeconds;
            sb.Append($"[{idx}:v]trim=start={N(s.In.TotalSeconds)}:end={N(s.Out.TotalSeconds)},setpts=PTS-STARTPTS,{norm}[v{i}];");
            concatIns.Append($"[v{i}]");
            if (hasAnyAudio)
            {
                if (s.HasAudio)
                    sb.Append($"[{idx}:a:0]atrim=start={N(s.In.TotalSeconds)}:end={N(s.Out.TotalSeconds)},asetpts=PTS-STARTPTS," +
                              $"aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
                else
                    sb.Append($"anullsrc=r=48000:cl=stereo,atrim=0:{N(len)}," +
                              $"asetpts=PTS-STARTPTS,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
                concatIns.Append($"[a{i}]");
            }
        }

        var effT = req.EffectiveTransitionSeconds;
        if (effT > 0)
        {
            var type = SafeTransition(req.TransitionType);
            var cum = (segs[0].Out - segs[0].In).TotalSeconds;
            var cur = "[v0]";
            for (var i = 1; i < segs.Count; i++)
            {
                var outl = i == segs.Count - 1 ? "[vc]" : $"[vt{i}]";
                sb.Append($"{cur}[v{i}]xfade=transition={type}:duration={N(effT)}:offset={N(cum - effT)}{outl};");
                cur = outl;
                cum += (segs[i].Out - segs[i].In).TotalSeconds - effT;
            }
            if (hasAnyAudio)
            {
                var acur = "[a0]";
                for (var i = 1; i < segs.Count; i++)
                {
                    var outl = i == segs.Count - 1 ? "[ac]" : $"[at{i}]";
                    sb.Append($"{acur}[a{i}]acrossfade=d={N(effT)}{outl};");
                    acur = outl;
                }
            }
        }
        else
        {
            sb.Append($"{concatIns}concat=n={segs.Count}:v=1:a={(hasAnyAudio ? 1 : 0)}[vc]{(hasAnyAudio ? "[ac]" : "")};");
        }

        var map = AppendMergedEffects(sb, req, plan, encoder, hasAnyAudio, mode == ReencodeMode.Discord ? 128 : 192);
        var inputs = string.Join(' ', inputList);
        status ??= $"Склейка {segs.Count} кусков…";
        var args = $"{inputs} -filter_complex \"{sb}\" {map} {EncoderArgs(encoder, plan, req)} -movflags +faststart {Ffmpeg.Q(output)}";
        await RunAsync(args, req.OutputLength, progress, status, ct);
    }

    async Task MultiMediaAudioAsync(ExportRequest req, string output, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var segs = req.MediaSegments!;
        if (!segs.Any(s => s.HasAudio)) throw new ExportException("В выбранных файлах нет звука");
        var inputList = new List<string>();

        var sb = new StringBuilder();
        var ins = new StringBuilder();
        for (var i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            var len = (s.Out - s.In).TotalSeconds;
            if (s.HasAudio)
            {
                var idx = inputList.Count;
                inputList.Add($"-i {Ffmpeg.Q(s.Path)}");
                sb.Append($"[{idx}:a:0]atrim=start={N(s.In.TotalSeconds)}:end={N(s.Out.TotalSeconds)},asetpts=PTS-STARTPTS," +
                          $"aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
            }
            else
                sb.Append($"anullsrc=r=48000:cl=stereo,atrim=0:{N(len)}," +
                          $"asetpts=PTS-STARTPTS,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
            ins.Append($"[a{i}]");
        }
        sb.Append($"{ins}concat=n={segs.Count}:v=0:a=1[ac];[ac]{AudioTailFilters(req)}[a]");
        var inputs = string.Join(' ', inputList);
        var args = $"{inputs} -filter_complex \"{sb}\" -map \"[a]\" -vn -c:a libmp3lame -b:a 192k -id3v2_version 3 {Ffmpeg.Q(output)}";
        await RunAsync(args, req.OutputLength, progress, $"Склейка звука {segs.Count} кусков…", ct);
    }

    async Task MultiMediaDiscordAsync(ExportRequest req, string output, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var plan = PlanVideo(req, ReencodeMode.Discord);
        long size = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var status = attempt == 0 ? "Склейка и сжатие под 10 МБ…" : $"Повторное сжатие ({attempt + 1}/3)…";
            await MultiMediaAsync(req, output, ReencodeMode.Discord, progress, ct, plan, status);
            size = new FileInfo(output).Length;
            if (size <= DiscordLimitBytes) return;
            var factor = (double)DiscordLimitBytes * 0.93 / size;
            plan = plan with { Kbps = Math.Max(150, (int)(plan.Kbps * factor)) };
            TryDelete(output);
        }
        throw new ExportException($"Не удалось уложиться в 10 МБ ({size / 1024.0 / 1024.0:0.0} МБ). Убери часть кусков.");
    }

    async Task MultiDiscordAsync(ExportRequest req, string output, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var plan = PlanVideo(req, ReencodeMode.Discord);
        long size = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var status = attempt == 0 ? "Склейка и сжатие под 10 МБ…" : $"Повторное сжатие ({attempt + 1}/3)…";
            await MultiCutAsync(req, output, ReencodeMode.Discord, progress, ct, plan, status);
            size = new FileInfo(output).Length;
            if (size <= DiscordLimitBytes) return;
            var factor = (double)DiscordLimitBytes * 0.93 / size;
            var kbps = Math.Max(150, (int)(plan.Kbps * factor));
            plan = plan with { Kbps = kbps };
            TryDelete(output);
        }
        throw new ExportException($"Не удалось уложиться в 10 МБ ({size / 1024.0 / 1024.0:0.0} МБ). Убери часть кусков.");
    }

    async Task MultiAudioAsync(ExportRequest req, string output, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        if (!req.Info.HasAudio) throw new ExportException("В клипе нет звуковой дорожки");
        var segs = req.EffectiveSegments;
        var sb = new StringBuilder();
        var ins = new StringBuilder();
        for (var i = 0; i < segs.Count; i++)
        {
            var (a, b) = segs[i];
            if (b <= a) throw new ExportException("Некорректный кусок (конец раньше начала)");
            sb.Append($"[0:a:0]atrim=start={N(a.TotalSeconds)}:end={N(b.TotalSeconds)},asetpts=PTS-STARTPTS[a{i}];");
            ins.Append($"[a{i}]");
        }
        sb.Append($"{ins}concat=n={segs.Count}:v=0:a=1[ac];[ac]{AudioTailFilters(req)}[a]");
        var args = $"-i {Ffmpeg.Q(req.SourcePath)} -filter_complex \"{sb}\" -map \"[a]\" -vn " +
                   $"-c:a libmp3lame -b:a 192k -id3v2_version 3 {Ffmpeg.Q(output)}";
        await RunAsync(args, req.OutputLength, progress, $"Склейка звука {segs.Count} кусков…", ct);
    }

    static bool Near(float a, float b) => Math.Abs(a - b) < 0.005f;

    static readonly HashSet<string> AllowedTransitions = new(StringComparer.Ordinal)
    { "fade", "dissolve", "slideleft", "slideright", "slideup", "slidedown",
      "wipeleft", "wiperight", "circleopen", "circleclose", "smoothleft", "smoothright", "fadeblack" };

    static string SafeTransition(string? t) => t is not null && AllowedTransitions.Contains(t) ? t : "fade";

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
        const string chain = "fps=15,scale='min(480,iw)':-2:flags=lanczos,split[a][b];" +
                             "[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle";
        var vf = req.SpeedChanged ? $"{Setpts(req)},{chain}" : chain;
        var args = $"{Seek(req)} -i {Ffmpeg.Q(req.SourcePath)} -map 0:v:0 -an -vf \"{vf}\" -loop 0 {Ffmpeg.Q(output)}";
        await RunAsync(args, req.OutputLength, progress, "Создание GIF…", ct);
    }

    async Task Mp3Async(ExportRequest req, string output, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        if (!req.Info.HasAudio) throw new ExportException("В клипе нет звуковой дорожки");
        var map = NeedsAudioFilter(req) ? $"-filter_complex \"{MixFilter(req)}\" -map \"[a]\"" : "-map 0:a:0";
        var args = $"{Seek(req)} -i {Ffmpeg.Q(req.SourcePath)} -vn {map} -c:a libmp3lame -b:a 192k -id3v2_version 3 {Ffmpeg.Q(output)}";
        await RunAsync(args, req.OutputLength, progress, "Кодирование MP3…", ct);
    }

    static VideoPlan PlanVideo(ExportRequest req, ReencodeMode mode)
    {
        var info = req.Info;

        int w = req.EffWidth > 0 ? req.EffWidth : info.Width;
        int h = req.EffHeight > 0 ? req.EffHeight : info.Height;
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
                var seconds = Math.Max(0.1, req.OutputLength.TotalSeconds);
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

    static bool NeedsAudioFilter(ExportRequest req) =>
        req.Info.HasAudio && (req.AudioChanged || req.SpeedChanged || req.HasFades);

    static string MixFilter(ExportRequest req)
    {
        var tempo = req.SpeedChanged ? "," + AtempoChain(req.Speed) : "";
        var fades = AudioFades(req);
        return req.Info.HasSeparateTracks
            ? $"[0:a:1]volume={G(req.SystemGain)}[s];[0:a:2]volume={G(req.MicGain)}[m];" +
              $"[s][m]amix=inputs=2:normalize=0{tempo}{fades},{Limiter}[a]"
            : $"[0:a:0]volume={G(req.MasterGain)}{tempo}{fades},{Limiter}[a]";
    }

    static string AtempoChain(double speed)
    {
        var rest = Math.Clamp(speed, ExportRequest.MinSpeed, ExportRequest.MaxSpeed);
        var steps = new List<double>();
        while (rest > 2.0) { steps.Add(2.0); rest /= 2.0; }
        while (rest < 0.5) { steps.Add(0.5); rest /= 0.5; }
        steps.Add(rest);
        return string.Join(',', steps.Select(s => "atempo=" + N(s)));
    }

    const string Limiter = "alimiter=limit=0.95:level=false:attack=5:release=50";

    static string AudioArgs(ExportRequest req, int kbps)
    {
        if (!req.Info.HasAudio) return "-an";
        if (NeedsAudioFilter(req)) return $"-filter_complex \"{MixFilter(req)}\" -map \"[a]\" -c:a aac -b:a {kbps}k";
        return req.Info.HasSeparateTracks ? $"-map 0:a -c:a aac -b:a {kbps}k" : $"-map 0:a:0 -c:a aac -b:a {kbps}k";
    }

    static string G(float gain) => Math.Clamp(gain, 0f, 4f).ToString("0.###", CultureInfo.InvariantCulture);

    static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    static string Setpts(ExportRequest req) =>
        $"setpts=PTS/{N(Math.Clamp(req.Speed, ExportRequest.MinSpeed, ExportRequest.MaxSpeed))}";

    static IEnumerable<string> RotateFilters(ExportRequest req)
    {
        switch (((req.Rotation % 360) + 360) % 360)
        {
            case 90: yield return "transpose=1"; break;
            case 180: yield return "transpose=1"; yield return "transpose=1"; break;
            case 270: yield return "transpose=2"; break;
        }
    }

    static IEnumerable<string> VideoFades(ExportRequest req)
    {
        var total = req.OutputLength.TotalSeconds;
        if (req.FadeIn > TimeSpan.Zero)
            yield return $"fade=t=in:st=0:d={N(Math.Min(req.FadeIn.TotalSeconds, total))}";
        if (req.FadeOut > TimeSpan.Zero)
        {
            var d = Math.Min(req.FadeOut.TotalSeconds, total);
            yield return $"fade=t=out:st={N(Math.Max(0, total - d))}:d={N(d)}";
        }
    }

    static string PrepareTextFile(string text)
    {
        Directory.CreateDirectory(AppPaths.TempDir);
        var path = Path.Combine(AppPaths.TempDir, $"text_{Guid.NewGuid():N}.txt");

        File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\r", "\n"), new UTF8Encoding(false));
        return path;
    }

    static string EscapeFilterPath(string p) => p.Replace('\\', '/').Replace(":", "\\\\:");

    static string BuildDrawText(ExportRequest req, string textFile)
    {
        var h = req.EffHeight > 0 ? req.EffHeight : 1080;
        var fontSize = Math.Clamp((int)Math.Round(h * 0.055 * Math.Clamp(req.TextScale, 0.4, 3.0)), 12, 400);
        var pad = Math.Max(24, (int)(h * 0.05));
        var y = req.TextPosition switch
        {
            TextPosition.Top => $"{pad}",
            TextPosition.Center => "(h-text_h)/2",
            _ => $"h-text_h-{pad}",
        };
        var font = FindFontFile();
        var fontOpt = font is null ? "" : $"fontfile={EscapeFilterPath(font)}:";
        return $"drawtext={fontOpt}textfile={EscapeFilterPath(textFile)}:expansion=none:fontcolor=white:fontsize={fontSize}:" +
               $"box=1:boxcolor=black@0.45:boxborderw={Math.Max(8, fontSize / 5)}:" +
               $"x=(w-text_w)/2:y={y}:line_spacing={Math.Max(4, fontSize / 6)}";
    }

    static string? FindFontFile()
    {
        if (_fontFile is not null) return _fontFile.Length == 0 ? null : _fontFile;
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates = ["segoeui.ttf", "arial.ttf", "tahoma.ttf", "calibri.ttf"];
        foreach (var f in candidates)
        {
            var p = Path.Combine(fonts, f);
            if (File.Exists(p)) { _fontFile = p; return p; }
        }
        _fontFile = "";
        return null;
    }
    static string? _fontFile;

    static string AudioFades(ExportRequest req)
    {
        var total = req.OutputLength.TotalSeconds;
        var parts = new List<string>();
        if (req.FadeIn > TimeSpan.Zero)
            parts.Add($"afade=t=in:st=0:d={N(Math.Min(req.FadeIn.TotalSeconds, total))}");
        if (req.FadeOut > TimeSpan.Zero)
        {
            var d = Math.Min(req.FadeOut.TotalSeconds, total);
            parts.Add($"afade=t=out:st={N(Math.Max(0, total - d))}:d={N(d)}");
        }
        return parts.Count > 0 ? "," + string.Join(',', parts) : "";
    }

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

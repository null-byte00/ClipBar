using System.Globalization;
using System.Text;
using ClipBar.Core;

namespace ClipBar.Capture;

internal sealed record EncoderSpec(string Id, string Family, string Codec, string Vendor, bool Hardware)
{
    public int Variants => Family is "nvenc" or "amf" ? 2 : 1;

    public string DisplayName(bool recommended) => Family switch
    {
        "x264" => "Процессор — x264 (нагружает CPU)",
        _ => $"{Vendor} — {CodecTitle}{(recommended ? " (рекомендуется)" : "")}",
    };

    string CodecTitle => Codec switch { "h264" => "H.264", "hevc" => "HEVC (H.265)", "av1" => "AV1", _ => Codec };

    public static readonly EncoderSpec[] All =
    [
        new("h264_qsv", "qsv", "h264", "Intel Quick Sync", true),
        new("hevc_qsv", "qsv", "hevc", "Intel Quick Sync", true),
        new("av1_qsv", "qsv", "av1", "Intel Quick Sync", true),
        new("h264_nvenc", "nvenc", "h264", "NVIDIA NVENC", true),
        new("hevc_nvenc", "nvenc", "hevc", "NVIDIA NVENC", true),
        new("h264_amf", "amf", "h264", "AMD AMF", true),
        new("hevc_amf", "amf", "hevc", "AMD AMF", true),
        new("libx264", "x264", "h264", "Процессор", false),
    ];

    public static EncoderSpec? Find(string id) => Array.Find(All, e => e.Id == id);
}

internal sealed record AudioLayout(string? SystemPipe, string? MicPipe, bool Separate)
{
    public bool HasSystem => SystemPipe is not null;
    public bool HasMic => MicPipe is not null;
    public bool Any => HasSystem || HasMic;
    public string[] TrackTitles => !Any ? [] : (HasSystem && HasMic && Separate) ? ["Микс", "Система", "Микрофон"] : ["Микс"];
    public static readonly AudioLayout None = new(null, null, false);
}

internal static class CaptureCommand
{
    public const string SegmentPattern = "s_%06d.ts";

    static string I(long v) => v.ToString(CultureInfo.InvariantCulture);
    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static string VideoFilter(AppSettings s, EncoderSpec enc, int variant, long t0us, int nativeHeight)
    {
        var fps = Math.Clamp(s.Fps, 10, 240);
        var sb = new StringBuilder();
        sb.Append($"ddagrab=output_idx={s.MonitorIndex}:framerate={fps}:draw_mouse={(s.CaptureCursor ? 1 : 0)}");
        sb.Append($",setpts=PTS-STARTPTS+(RTCSTART-{I(t0us)})/(1000000*TB)");

        var scale = s.OutputHeight > 0 && nativeHeight > 0 && s.OutputHeight < nativeHeight;
        var outH = scale ? s.OutputHeight & ~1 : 0;

        switch (enc.Family)
        {
            case "qsv":
                // Надёжный путь: скачиваем кадры в системную память вместо GPU-side vpp_qsv,
                // который на Intel Arc периодически падает с "Conversion failed". h264_qsv
                // кодирует из системного nv12 (загружает на GPU сам). Стабильнее, ценой копии кадра.
                sb.Append(",hwdownload,format=bgra");
                if (scale) sb.Append($",scale=-2:{outH}:flags=fast_bilinear");
                sb.Append(",format=nv12");
                break;
            case "nvenc":
            case "amf":
                if (variant == 0 && !scale) break;
                sb.Append(",hwdownload,format=bgra");
                if (scale) sb.Append($",scale=-2:{outH}:flags=fast_bilinear");
                sb.Append(",format=nv12");
                break;
            default:
                sb.Append(",hwdownload,format=bgra");
                sb.Append(scale ? $",scale=-2:{outH}:flags=fast_bilinear" : ",scale=flags=fast_bilinear");
                sb.Append(",format=yuv420p");
                break;
        }
        sb.Append("[v]");
        return sb.ToString();
    }

    public static string EncoderOptions(AppSettings s, EncoderSpec enc)
    {
        var fps = Math.Clamp(s.Fps, 10, 240);
        var kb = Math.Clamp(s.VideoBitrateKbps, 500, 200_000);
        var rc = $"-b:v {kb}k -maxrate {kb * 3 / 2}k -bufsize {kb * 2}k -bf 0 -g {fps}";
        var opts = enc.Family switch
        {
            "qsv" => $"-c:v {enc.Id} -preset veryfast {rc}" + (enc.Codec == "hevc" ? " -idr_interval 1" : ""),
            "nvenc" => $"-c:v {enc.Id} -preset p4 -tune ll -rc vbr {rc} -no-scenecut 1 -forced-idr 1",
            "amf" => $"-c:v {enc.Id} -quality balanced -rc vbr_peak {rc}",
            _ => $"-c:v libx264 -preset ultrafast {rc} -keyint_min {fps} -sc_threshold 0 -pix_fmt yuv420p",
        };
        return opts + " -fps_mode:v cfr";
    }

    static string AudioInput(string pipe, int offsetMs)
    {
        var sb = new StringBuilder("-use_wallclock_as_timestamps 1 -thread_queue_size 4096 -f f32le -ar 48000 -ac 2");
        if (offsetMs != 0) sb.Append($" -itsoffset {F(offsetMs / 1000.0)}");
        sb.Append($" -i {Ffmpeg.Q(pipe)}");
        return sb.ToString();
    }

    static string AudioFilter(AudioLayout a, int firstInput, long t0us)
    {
        var anchor = $"asetpts=STARTPTS-{I(t0us)}/(1000000*TB)+N/SR/TB,aresample=async=1:min_hard_comp=0.01:first_pts=0";
        var sb = new StringBuilder();
        var i = firstInput;
        if (a.HasSystem && a.HasMic)
        {
            var mix = "amix=inputs=2:normalize=0:dropout_transition=0,alimiter=limit=0.98:level=false:latency=1";
            if (a.Separate)
            {
                sb.Append($";[{i}:a]{anchor},asplit=2[sys][sysm]");
                sb.Append($";[{i + 1}:a]{anchor},asplit=2[mic][micm]");
                sb.Append($";[sysm][micm]{mix}[mix]");
            }
            else
            {
                sb.Append($";[{i}:a]{anchor}[sysm];[{i + 1}:a]{anchor}[micm];[sysm][micm]{mix}[mix]");
            }
        }
        else if (a.Any)
        {
            sb.Append($";[{i}:a]{anchor}[mix]");
        }
        return sb.ToString();
    }

    static string AudioMaps(AudioLayout a, int audioKbps)
    {
        if (!a.Any) return "-an";
        var maps = a.HasSystem && a.HasMic && a.Separate ? "-map \"[mix]\" -map \"[sys]\" -map \"[mic]\"" : "-map \"[mix]\"";
        return $"{maps} -c:a aac -b:a {Math.Clamp(audioKbps, 64, 512)}k -ar 48000";
    }

    public static string BuildCapture(AppSettings s, EncoderSpec enc, int variant, AudioLayout audio, long t0us,
        int nativeHeight, int startNumber, string listPath, string bufferDir)
    {
        var sb = new StringBuilder("-hide_banner -loglevel info -nostats -y -copyts ");
        var inputs = 0;
        if (audio.HasSystem) { sb.Append(AudioInput(audio.SystemPipe!, s.AudioOffsetMs)).Append(' '); inputs++; }
        if (audio.HasMic) { sb.Append(AudioInput(audio.MicPipe!, s.AudioOffsetMs)).Append(' '); inputs++; }

        var graph = VideoFilter(s, enc, variant, t0us, nativeHeight) + AudioFilter(audio, 0, t0us);
        sb.Append($"-filter_complex \"{graph}\" ");
        sb.Append($"-map \"[v]\" {EncoderOptions(s, enc)} ");
        sb.Append(AudioMaps(audio, s.AudioBitrateKbps)).Append(' ');
        sb.Append("-f segment -segment_time 1 -segment_format mpegts -reset_timestamps 0 ");
        sb.Append($"-segment_start_number {startNumber} -segment_list {Ffmpeg.Q(listPath)} -segment_list_type csv ");
        sb.Append(Ffmpeg.Q(System.IO.Path.Combine(bufferDir, SegmentPattern)));
        _ = inputs;
        return sb.ToString();
    }

    public static string BuildProbe(AppSettings s, EncoderSpec enc, int variant, int nativeHeight, double seconds, string outPath)
    {
        var t0us = UnixMicros(DateTime.UtcNow);
        var probe = s.Clone();
        probe.OutputHeight = 0;
        var sb = new StringBuilder("-hide_banner -loglevel error -nostats -y -copyts ");
        sb.Append($"-filter_complex \"{VideoFilter(probe, enc, variant, t0us, nativeHeight)}\" ");
        sb.Append($"-map \"[v]\" {EncoderOptions(probe, enc)} -an -t {F(seconds)} -f mpegts {Ffmpeg.Q(outPath)}");
        return sb.ToString();
    }

    public static long UnixMicros(DateTime utc) => (utc - DateTime.UnixEpoch).Ticks / 10;
}

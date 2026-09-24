using System.Globalization;
using System.IO;
using System.Text.Json;
using ClipBar.Core;

namespace ClipBar.Editor;

public sealed record ClipMediaInfo(
    TimeSpan Duration,
    int Width,
    int Height,
    double Fps,
    string VideoCodec,
    long VideoBitrate,
    int AudioTracks,
    string? AudioCodec,
    long SizeBytes)
{
    public bool HasSeparateTracks => AudioTracks >= 3;
    public bool HasVideo => Width > 0 && Height > 0;
    public bool HasAudio => AudioTracks > 0;
    public TimeSpan FrameDuration => TimeSpan.FromSeconds(1.0 / (Fps > 1 ? Fps : 30));

    public static async Task<ClipMediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден", path);

        var r = await Ffmpeg.ProbeAsync($"-v error -print_format json -show_streams -show_format {Ffmpeg.Q(path)}", ct);
        if (!r.Ok)
            throw new InvalidOperationException(LastLine(r.StdErr) ?? "ffprobe не смог прочитать файл");

        using var doc = JsonDocument.Parse(r.StdOut);
        var root = doc.RootElement;

        int w = 0, h = 0, audio = 0;
        double fps = 0;
        long vBitrate = 0, aBitrateSum = 0;
        string vCodec = "", aCodec = "";
        TimeSpan streamDuration = TimeSpan.Zero;

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type");
                if (type == "video" && w == 0)
                {
                    if (s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1)
                        continue;
                    w = Int(s, "width");
                    h = Int(s, "height");
                    vCodec = Str(s, "codec_name");
                    fps = ParseRate(Str(s, "avg_frame_rate"));
                    if (fps <= 0) fps = ParseRate(Str(s, "r_frame_rate"));
                    vBitrate = Long(s, "bit_rate");
                    streamDuration = TimeSpan.FromSeconds(Dbl(s, "duration"));
                }
                else if (type == "audio")
                {
                    if (audio == 0) aCodec = Str(s, "codec_name");
                    audio++;
                    aBitrateSum += Long(s, "bit_rate");
                }
            }
        }

        var duration = TimeSpan.Zero;
        long size = 0, formatBitrate = 0;
        if (root.TryGetProperty("format", out var fmt))
        {
            duration = TimeSpan.FromSeconds(Dbl(fmt, "duration"));
            size = Long(fmt, "size");
            formatBitrate = Long(fmt, "bit_rate");
        }
        if (duration <= TimeSpan.Zero) duration = streamDuration;
        if (duration <= TimeSpan.Zero)
            throw new InvalidOperationException("Не удалось определить длительность клипа");
        if (size == 0) { try { size = new FileInfo(path).Length; } catch { } }
        if (vBitrate == 0 && w > 0 && formatBitrate > aBitrateSum)
            vBitrate = formatBitrate - aBitrateSum;

        return new ClipMediaInfo(duration, w, h, fps, vCodec, vBitrate, audio, audio > 0 ? aCodec : null, size);
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    static long Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt64();
        return long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0;
    }

    static double Dbl(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    static double ParseRate(string s)
    {
        var slash = s.IndexOf('/');
        if (slash < 0) return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        if (!double.TryParse(s.AsSpan(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return 0;
        if (!double.TryParse(s.AsSpan(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var den) || den <= 0) return 0;
        return num / den;
    }

    static string? LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : null;
    }
}

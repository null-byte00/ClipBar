using System.IO;
using System.Media;
using ClipBar.Core;

namespace ClipBar.UI.Notifications;

public enum ToastSound { None, Saved, RecordStart, RecordStop, Error }

public static class ToastSounds
{
    const int Rate = 44100;
    const double Peak = 0.05;
    static readonly Lazy<Dictionary<ToastSound, string>> Files = new(Render);

    public static void Play(ToastSound sound)
    {
        if (sound == ToastSound.None) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (!Files.Value.TryGetValue(sound, out var path) || !File.Exists(path)) return;
                using var player = new SoundPlayer(path);
                player.Play();
            }
            catch (Exception ex) { Log.Warn($"Sound {sound} failed: {ex.Message}"); }
        });
    }

    static Dictionary<ToastSound, string> Render()
    {
        var dir = Path.Combine(AppPaths.DataDir, "sounds");
        var map = new Dictionary<ToastSound, string>();
        try
        {
            Directory.CreateDirectory(dir);
            map[ToastSound.Saved] = Write(dir, "saved.wav", Ding());
            map[ToastSound.RecordStart] = Write(dir, "rec-start.wav", TwoTone(587.33, 880.00));
            map[ToastSound.RecordStop] = Write(dir, "rec-stop.wav", TwoTone(880.00, 587.33));
            map[ToastSound.Error] = Write(dir, "error.wav", ErrorTone());
        }
        catch (Exception ex) { Log.Warn($"Sound rendering failed: {ex.Message}"); }
        return map;
    }

    static float[] Ding()
    {
        var n = (int)(Rate * 0.42);
        var buf = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)Rate;
            var env = Math.Min(1, t / 0.004) * Math.Exp(-t * 9.0);
            buf[i] = (float)(env * (Math.Sin(2 * Math.PI * 1046.5 * t) * 0.8 + Math.Sin(2 * Math.PI * 2093.0 * t) * 0.2));
        }
        return buf;
    }

    static float[] TwoTone(double f1, double f2)
    {
        const double note = 0.11, gap = 0.02;
        var n = (int)(Rate * (note * 2 + gap + 0.12));
        var buf = new float[n];
        AddNote(buf, 0, note + 0.1, f1);
        AddNote(buf, note + gap, note + 0.12, f2);
        return buf;
    }

    static float[] ErrorTone()
    {
        var n = (int)(Rate * 0.32);
        var buf = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)Rate;
            var env = Math.Min(1, t / 0.006) * Math.Exp(-t * 11.0);
            buf[i] = (float)(env * (Math.Sin(2 * Math.PI * 261.6 * t) * 0.7 + Math.Sin(2 * Math.PI * 392.0 * t) * 0.3));
        }
        return buf;
    }

    static void AddNote(float[] buf, double start, double length, double freq)
    {
        var from = (int)(start * Rate);
        var count = Math.Min((int)(length * Rate), buf.Length - from);
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)Rate;
            var env = Math.Min(1, t / 0.008) * Math.Min(1, Math.Exp(-(t - 0.06) * 14.0));
            if (t < 0.06) env = Math.Min(1, t / 0.008);
            buf[from + i] += (float)(env * (Math.Sin(2 * Math.PI * freq * t) * 0.85 + Math.Sin(2 * Math.PI * freq * 2 * t) * 0.15));
        }
    }

    static string Write(string dir, string name, float[] samples)
    {
        var path = Path.Combine(dir, name);
        var max = samples.Max(Math.Abs);
        var gain = max > 0 ? Peak / max : 0;
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        var dataBytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples) w.Write((short)Math.Round(Math.Clamp(s * gain, -1, 1) * short.MaxValue));
        return path;
    }
}

using System.IO;

namespace ClipBar.Core;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipBar");

    public static string BufferDir => Path.Combine(DataDir, "buffer");
    public static string ThumbsDir => Path.Combine(DataDir, "thumbs");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string TempDir => Path.Combine(DataDir, "temp");

    public static string SettingsFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipBar", "settings.json");

    public static string FfmpegPath => _ffmpeg ??= FindTool("ffmpeg.exe");
    public static string FfprobePath => _ffprobe ??= FindTool("ffprobe.exe");
    static string? _ffmpeg, _ffprobe;

    static string FindTool(string exe)
    {
        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "tools", exe),
            Path.Combine(baseDir, exe),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "tools", exe)),
        ];
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return exe;
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(BufferDir);
        Directory.CreateDirectory(ThumbsDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(TempDir);
    }
}

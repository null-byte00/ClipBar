using System.IO;

namespace ClipBar.Core;

public static class Log
{
    static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                File.AppendAllText(Path.Combine(AppPaths.LogsDir, $"clipbar-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
        }
        catch { }
    }
}

using System.IO;
using ClipBar.Core;

namespace ClipBar.Library;

// Следит за папкой скриншотов Windows (Win+Shift+S / Snipping Tool)
// и импортирует новые файлы в коллекцию ClipBar.
public sealed class ScreenshotWatcher : IDisposable
{
    readonly List<FileSystemWatcher> _watchers = new();
    readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();

    public ScreenshotWatcher()
    {
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OneDrive", "Pictures", "Screenshots"),
        };
        foreach (var dir in dirs.Where(Directory.Exists).Distinct())
        {
            var w = new FileSystemWatcher(dir, "*.png") { IncludeSubdirectories = false, EnableRaisingEvents = false };
            w.Created += OnCreated;
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
            _ = Task.Run(() => ImportExisting(dir));
            Log.Info("ScreenshotWatcher: слежу за " + dir);
        }
        if (_watchers.Count == 0) Log.Info("ScreenshotWatcher: папка скриншотов не найдена");
    }

    // Разовый импорт того, что уже лежит в папке
    async Task ImportExisting(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).Take(200))
            {
                lock (_gate) _seen.Add(Path.GetFullPath(f));
                _ = AppServices.Library?.AddAsync(f);
            }
        }
        catch (Exception ex) { Log.Warn("ScreenshotWatcher import failed: " + ex.Message); }
    }

    void OnCreated(object sender, FileSystemEventArgs e)
    {
        // Snipping Tool пишет файл не мгновенно — AddAsync сам дождётся readable
        Task.Delay(800).ContinueWith(_ =>
        {
            try
            {
                if (!File.Exists(e.FullPath)) return;
                lock (_gate)
                {
                    if (!_seen.Add(Path.GetFullPath(e.FullPath))) return;
                }
                _ = AppServices.Library?.AddAsync(e.FullPath);
                Log.Info("ScreenshotWatcher: импортирован " + Path.GetFileName(e.FullPath));
            }
            catch (Exception ex) { Log.Warn("ScreenshotWatcher.OnCreated: " + ex.Message); }
        });
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}

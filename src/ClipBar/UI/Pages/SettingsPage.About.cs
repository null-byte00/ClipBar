using System.Reflection;
using System.Windows;
using ClipBar.Core;

namespace ClipBar.UI.Pages;

public partial class SettingsPage
{
    bool _aboutLoaded;

    void LoadAboutStatic()
    {
        var asm = typeof(SettingsPage).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = !string.IsNullOrWhiteSpace(info) ? info.Split('+')[0] : asm.GetName().Version?.ToString(3) ?? "1.0.0";
        VersionText.Text = $"Версия {version} · Windows 11 · .NET {Environment.Version.Major}";
    }

    async Task LoadAboutAsync()
    {
        if (_aboutLoaded) return;
        _aboutLoaded = true;

        try
        {
            var gpus = await Task.Run(SettingsNative.GetGpuNames);
            GpuHeader.Description = gpus.Count > 0 ? string.Join(" · ", gpus) : "Не удалось определить";
        }
        catch (Exception ex)
        {
            Log.Warn("GPU name lookup failed: " + ex.Message);
            GpuHeader.Description = "Не удалось определить";
        }

        try
        {
            var path = AppPaths.FfmpegPath;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await Ffmpeg.RunAsync("-version", cts.Token);
            var first = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            var version = ParseFfmpegVersion(first);
            FfmpegHeader.Description = version is not null ? $"Версия {version} · {path}" : $"Найден, но версия не распознана · {path}";
            FfmpegHeader.Warning = "";
        }
        catch (Exception ex)
        {
            Log.Error("ffmpeg -version failed", ex);
            FfmpegHeader.Description = "Не найден. Положи ffmpeg.exe и ffprobe.exe в папку tools рядом с программой";
            FfmpegHeader.Warning = "Без FFmpeg запись невозможна";
        }
    }

    internal static string? ParseFfmpegVersion(string firstLine)
    {
        const string marker = "ffmpeg version ";
        var i = firstLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var token = firstLine[(i + marker.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('-', 2);
        if (parts.Length == 1) return parts[0];
        var flavour = parts[1].Replace("_build", " build").Replace("www.", "");
        return $"{parts[0]} ({flavour.Replace('-', ',').Replace(",", ", ")})";
    }

    void OnOpenLogsClick(object sender, RoutedEventArgs e) => OpenInExplorer(AppPaths.LogsDir);

    async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            if (!UpdateService.IsInstalledCopy)
                AppServices.Notifier?.Show("Обновления — только для установленной версии",
                    "Эта копия запущена не из установщика (сборка из исходников)", NotifyKind.Info);
            else await UpdateService.CheckAsync(userInitiated: true);
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }
}

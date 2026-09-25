using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;

namespace ClipBar.Core;

public static class UpdateService
{
    public const string Repo = "null-byte00/ClipBar";
    static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(45);
    static readonly TimeSpan Every = TimeSpan.FromHours(6);
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipBar";

    static Timer? _timer;
    static readonly SemaphoreSlim Gate = new(1, 1);
    static Version? _offeredVersion;

    public static Version Current => typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
    static string UpdatesDir => Path.Combine(AppPaths.DataDir, "updates");
    static string PendingFile => Path.Combine(UpdatesDir, "pending.json");

    public static bool IsInstalledCopy
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
                var dir = (key?.GetValue("InstallLocation") as string)?.TrimEnd('\\');
                return dir is not null && string.Equals(dir, AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static bool ApplyPendingAtStartup()
    {
        try
        {
            if (!IsInstalledCopy || !File.Exists(PendingFile)) return false;
            var p = JsonSerializer.Deserialize<Pending>(File.ReadAllText(PendingFile));
            if (p is null || !Version.TryParse(p.Version, out var v) || v <= Current || !File.Exists(p.Path))
            {
                Cleanup();
                return false;
            }
            Log.Info($"Update: applying downloaded {p.Version} at startup");
            File.Delete(PendingFile);
            Launch(p.Path);
            return true;
        }
        catch (Exception ex) { Log.Warn("Update: pending apply failed: " + ex.Message); return false; }
    }

    public static void Start()
    {
        if (!IsInstalledCopy) { Log.Info("Update: dev/portable copy — automatic updates are off"); return; }
        Cleanup(onlyOld: true);
        _timer = new Timer(_ => _ = CheckAsync(userInitiated: false), null, FirstCheck, Every);
    }

    public sealed record Release(Version Version, string Tag, string AssetUrl, long Size, string Notes);

    public static async Task<Release?> GetLatestAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ClipBar/{Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("ClipBar-Setup", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) continue;
            return new Release(version, tag, url, a.GetProperty("size").GetInt64(),
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "");
        }
        return null;
    }

    public static async Task CheckAsync(bool userInitiated)
    {
        if (!await Gate.WaitAsync(0)) return;
        try
        {
            var latest = await GetLatestAsync();
            if (latest is null || latest.Version <= Current)
            {
                if (userInitiated) AppServices.Notifier?.Show("Обновлений нет", $"У тебя последняя версия — {Current.ToString(3)}", NotifyKind.Info);
                return;
            }
            Log.Info($"Update: {latest.Tag} available (current {Current})");
            var path = await DownloadAsync(latest);
            if (path is null)
            {
                if (userInitiated) AppServices.Notifier?.Show("Не удалось скачать обновление", "Проверь интернет и попробуй ещё раз", NotifyKind.Warning);
                return;
            }
            if (_offeredVersion == latest.Version && !userInitiated) return;
            _offeredVersion = latest.Version;
            AppServices.Notifier?.ShowAction($"Обновление ClipBar {latest.Version.ToString(3)} готово",
                "Установится само при следующем запуске", NotifyKind.Success, "Нажми, чтобы установить сейчас",
                () => InstallNow(path));
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed: " + ex.Message);
            if (userInitiated) AppServices.Notifier?.Show("Не удалось проверить обновления", ex.Message, NotifyKind.Warning);
        }
        finally { Gate.Release(); }
    }

    static async Task<string?> DownloadAsync(Release r)
    {
        Directory.CreateDirectory(UpdatesDir);
        var target = Path.Combine(UpdatesDir, $"ClipBar-Setup-{r.Version.ToString(3)}.exe");
        if (File.Exists(target) && new FileInfo(target).Length == r.Size) return Remember(r, target);

        var part = target + ".part";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ClipBar/{Current}");
        using (var resp = await http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            if (!resp.IsSuccessStatusCode) return null;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            await src.CopyToAsync(dst);
        }
        if (new FileInfo(part).Length != r.Size) { File.Delete(part); return null; }
        File.Move(part, target, overwrite: true);
        return Remember(r, target);
    }

    sealed record Pending(string Version, string Path);

    static string Remember(Release r, string path)
    {
        File.WriteAllText(PendingFile, JsonSerializer.Serialize(new Pending(r.Version.ToString(3), path)));
        return path;
    }

    static void InstallNow(string path)
    {
        if (AppServices.Engine?.IsRecording == true)
        {
            AppServices.Notifier?.Show("Идёт запись", "Останови запись — и обновление установится", NotifyKind.Warning);
            return;
        }
        Launch(path);
        AppServices.Shell?.ExitApp();
    }

    static void Launch(string setup) =>
        Process.Start(new ProcessStartInfo(setup, "--update") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(setup) });

    static void Cleanup(bool onlyOld = false)
    {
        try
        {
            if (!Directory.Exists(UpdatesDir)) return;
            foreach (var f in Directory.EnumerateFiles(UpdatesDir, "ClipBar-Setup-*.exe"))
            {
                var name = Path.GetFileNameWithoutExtension(f)["ClipBar-Setup-".Length..];
                if (!onlyOld || (Version.TryParse(name, out var v) && v <= Current)) File.Delete(f);
            }
            if (!onlyOld) File.Delete(PendingFile);
        }
        catch { }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ClipBar.Core;

public sealed record FfResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

public static class Ffmpeg
{
    public static Task<FfResult> RunAsync(string args, CancellationToken ct = default, Action<TimeSpan>? onProgress = null)
        => RunToolAsync(AppPaths.FfmpegPath, args, ct, onProgress);

    public static Task<FfResult> ProbeAsync(string args, CancellationToken ct = default)
        => RunToolAsync(AppPaths.FfprobePath, args, ct, null);

    public static Process StartLongRunning(string args, Action<string>? onStderr)
    {
        var p = new Process { StartInfo = CreateStartInfo(AppPaths.FfmpegPath, args), EnableRaisingEvents = true };
        p.StartInfo.RedirectStandardInput = true;
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) onStderr?.Invoke(e.Data); };
        p.OutputDataReceived += (_, _) => { };
        p.Start();
        ChildProcessJob.Assign(p);
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        return p;
    }

    public static async Task StopGracefullyAsync(Process p, TimeSpan timeout)
    {
        try
        {
            if (p.HasExited) return;
            await p.StandardInput.WriteAsync('q').ConfigureAwait(false);
            await p.StandardInput.FlushAsync().ConfigureAwait(false);
            using var cts = new CancellationTokenSource(timeout);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await p.WaitForExitAsync(killCts.Token).ConfigureAwait(false);
            }
            catch { }
        }
    }

    public static string Q(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

    public static string Sec(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    static ProcessStartInfo CreateStartInfo(string exe, string args) => new(exe, args)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    static async Task<FfResult> RunToolAsync(string exe, string args, CancellationToken ct, Action<TimeSpan>? onProgress)
    {
        using var p = new Process { StartInfo = CreateStartInfo(exe, args) };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            if (onProgress is not null && e.Data.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(e.Data.AsSpan(12), out var us) && us >= 0)
                onProgress(TimeSpan.FromMicroseconds(us));
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        p.Start();
        ChildProcessJob.Assign(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        TryLowerPriority(p);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        p.WaitForExit();
        return new FfResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    static void TryLowerPriority(Process p)
    {
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
    }
}

using ClipBar.Core;

namespace ClipBar.Editor;

public sealed class ExportJob
{
    public static ExportJob? Current { get; private set; }

    readonly CancellationTokenSource _cts = new();
    readonly object _ctsLock = new();

    ExportJob(ExportRequest request) { Request = request; }

    public ExportRequest Request { get; }
    public double Fraction { get; private set; }
    public string Status { get; private set; } = "Подготовка…";
    public string? OutputPath { get; private set; }
    public string? Error { get; private set; }
    public bool IsCancelled { get; private set; }
    public bool IsDone { get; private set; }
    public bool Succeeded => IsDone && OutputPath is not null;

    public event Action<ExportJob>? Changed;

    public static ExportJob Start(ExportRequest request)
    {
        var job = new ExportJob(request);
        Current = job;
        job.Run();
        return job;
    }

    public void Cancel()
    {
        lock (_ctsLock)
        {
            if (IsDone) return;
            IsCancelled = true;
            Status = "Отмена…";
            try { _cts.Cancel(); } catch (ObjectDisposedException) { return; }
        }
        Changed?.Invoke(this);
    }

    public static void Clear() { if (Current?.IsDone == true) Current = null; }

    async void Run()
    {
        try
        {
            OutputPath = await new ExportService().ExportAsync(Request, new DirectProgress(this), _cts.Token);
            Fraction = 1;
            Status = "Готово";
        }
        catch (OperationCanceledException)
        {
            IsCancelled = true;
            Status = "Отменено";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "Ошибка";
            Log.Error("Export failed", ex);
            try { AppServices.Notifier?.Show("Ошибка экспорта", ex.Message, NotifyKind.Error); } catch { }
        }
        finally
        {
            lock (_ctsLock)
            {
                IsDone = true;
                _cts.Dispose();
            }
            Changed?.Invoke(this);
        }
    }

    sealed class DirectProgress(ExportJob job) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value)
        {
            if (job.IsDone) return;
            job.Fraction = value.Fraction;
            if (!job.IsCancelled) job.Status = value.Status;
            job.Changed?.Invoke(job);
        }
    }
}

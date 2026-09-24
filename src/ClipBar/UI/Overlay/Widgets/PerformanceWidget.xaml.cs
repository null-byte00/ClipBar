using System.Windows;
using System.Windows.Controls;
using ClipBar.Core;
using Wpf.Ui.Controls;

namespace ClipBar.UI.Overlay.Widgets;

public partial class PerformanceWidget : UserControl, IOverlayWidget
{
    public string Id => "performance";
    public string Title => "Производительность";
    public SymbolRegular Icon => SymbolRegular.TopSpeed24;
    public IOverlayHost? Host { get; set; }
    public bool NeedsFastTick => false;

    const int HistoryLength = 60;
    readonly PerformanceSampler _sampler = new();
    readonly Dictionary<string, List<double>> _history = new()
    {
        ["cpu"] = new(HistoryLength + 1), ["gpu"] = new(HistoryLength + 1), ["ram"] = new(HistoryLength + 1), ["app"] = new(HistoryLength + 1),
    };
    string _selected = "cpu";
    bool _active;
    bool _gpuMissingLogged;

    public PerformanceWidget()
    {
        InitializeComponent();
        Graph.Capacity = HistoryLength;
        _sampler.Sampled += OnSampled;
    }

    public void Activate()
    {
        if (_active) return;
        _active = true;
        _sampler.Start();
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _sampler.Stop();
    }

    public void FastTick() { }
    public void SlowTick() { }

    void OnSampled(PerfSample s)
    {
        if (!_active) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_active) return;
            try { Apply(s); } catch (Exception ex) { Log.Error("PerformanceWidget update failed", ex); }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    void Apply(PerfSample s)
    {
        Push("cpu", s.CpuPercent);
        Push("gpu", s.GpuPercent ?? 0);
        Push("ram", s.RamTotalGb > 0 ? 100.0 * s.RamUsedGb / s.RamTotalGb : 0);
        Push("app", s.AppCpuPercent);

        CpuValue.Text = $"{Math.Round(s.CpuPercent)}%";
        if (s.GpuPercent is { } g) GpuValue.Text = $"{Math.Round(g)}%";
        else
        {
            GpuValue.Text = "—";
            GpuRow.ToolTip = "Счётчик загрузки GPU недоступен на этом ПК";
            if (!_gpuMissingLogged) { _gpuMissingLogged = true; Log.Warn("GPU Engine counter unavailable"); }
        }
        RamValue.Text = s.RamTotalGb > 0 ? $"{s.RamUsedGb:0.0} ГБ" : "—";
        AppValue.Text = $"{s.AppCpuPercent:0.0}%";
        Footprint.Text = s.RamTotalGb > 0
            ? $"ClipBar + ffmpeg: {s.AppMemoryMb:0} МБ · ОЗУ {s.RamUsedGb:0.0} из {s.RamTotalGb:0} ГБ"
            : $"ClipBar + ffmpeg: {s.AppMemoryMb:0} МБ";

        UpdateGraph();
    }

    void Push(string key, double v)
    {
        var list = _history[key];
        list.Add(v);
        if (list.Count > HistoryLength) list.RemoveAt(0);
    }

    void Row_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key }) { _selected = key; UpdateGraph(); }
    }

    void UpdateGraph()
    {
        if (Graph is null) return;
        var list = _history[_selected];
        double max = 100;
        if (_selected == "app")
        {
            var peak = list.Count > 0 ? list.Max() : 0;
            max = peak <= 4 ? 5 : peak <= 9 ? 10 : peak <= 24 ? 25 : peak <= 49 ? 50 : 100;
        }
        Graph.Maximum = max;
        AxisTop.Text = $"{max:0}%";
        GraphTitle.Text = _selected switch
        {
            "cpu" => "Процессор",
            "gpu" => "Видеокарта (3D)",
            "ram" => "Память",
            _ => "ClipBar + ffmpeg",
        };
        Graph.SetValues(list);
    }

    internal bool IsSampling => _active;
}

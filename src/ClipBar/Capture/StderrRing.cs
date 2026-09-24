namespace ClipBar.Capture;

internal sealed class StderrRing
{
    readonly int _capacity;
    readonly Queue<string> _lines;
    readonly object _lock = new();

    public StderrRing(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _lines = new Queue<string>(_capacity);
    }

    public void Add(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_lock)
        {
            if (_lines.Count >= _capacity) _lines.Dequeue();
            _lines.Enqueue(line);
        }
    }

    public void Clear() { lock (_lock) _lines.Clear(); }

    public string[] Snapshot() { lock (_lock) return [.. _lines]; }

    public string? LastError()
    {
        var lines = Snapshot();
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var l = lines[i];
            if (l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Invalid", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Cannot", StringComparison.OrdinalIgnoreCase))
                return Trim(l);
        }
        return lines.Length > 0 ? Trim(lines[^1]) : null;
    }

    public bool Any(params string[] needles)
    {
        var lines = Snapshot();
        foreach (var l in lines)
            foreach (var n in needles)
                if (l.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string Trim(string l)
    {
        if (l.StartsWith('[') && l.IndexOf(']') is var i && i > 0 && i + 1 < l.Length) l = l[(i + 1)..].TrimStart();
        return l.Length > 160 ? l[..160] + "…" : l;
    }
}

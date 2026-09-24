using System.Globalization;
using System.IO;

namespace ClipBar.Capture;

internal sealed record SegmentInfo(int Index, int Epoch, string Path, double Start, double End, DateTime CompletedAt)
{
    public double Duration => Math.Max(0, End - Start);
}

internal sealed class SegmentHold(int from, int to)
{
    public int From { get; } = from;
    public int To { get; set; } = to;
    public bool Covers(int index) => index >= From && index <= To;
}

internal sealed class SegmentStore
{
    readonly object _lock = new();
    readonly SortedDictionary<int, SegmentInfo> _segments = new();
    readonly List<SegmentHold> _holds = new();
    TaskCompletionSource _changed = NewTcs();

    static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int LastIndex { get { lock (_lock) return _segments.Count == 0 ? -1 : _segments.Keys.Last(); } }

    public int Count { get { lock (_lock) return _segments.Count; } }

    public void Add(SegmentInfo seg)
    {
        lock (_lock)
        {
            _segments[seg.Index] = seg;
            var tcs = _changed;
            _changed = NewTcs();
            tcs.TrySetResult();
        }
    }

    public async Task<bool> WaitForIndexAsync(int index, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_segments.ContainsKey(index) || (_segments.Count > 0 && _segments.Keys.Last() >= index)) return true;
                wait = _changed.Task;
            }
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;
            try { await wait.WaitAsync(remaining, ct); }
            catch (TimeoutException) { return false; }
            catch (OperationCanceledException) { return false; }
        }
    }

    public SegmentInfo? Get(int index) { lock (_lock) return _segments.GetValueOrDefault(index); }

    public List<SegmentInfo> All() { lock (_lock) return [.. _segments.Values]; }

    public List<SegmentInfo> OfEpoch(int epoch) { lock (_lock) return _segments.Values.Where(s => s.Epoch == epoch).ToList(); }

    public List<SegmentInfo> Range(int from, int to) { lock (_lock) return _segments.Values.Where(s => s.Index >= from && s.Index <= to).ToList(); }

    public double DurationOfEpoch(int epoch) { lock (_lock) return _segments.Values.Where(s => s.Epoch == epoch).Sum(s => s.Duration); }

    public List<SegmentInfo> Newest(int epoch, double seconds)
    {
        lock (_lock)
        {
            var result = new List<SegmentInfo>();
            double acc = 0;
            foreach (var s in _segments.Values.Reverse())
            {
                if (s.Epoch != epoch) continue;
                result.Add(s);
                acc += s.Duration;
                if (acc >= seconds - 0.001) break;
            }
            result.Reverse();
            return result;
        }
    }

    public (List<SegmentInfo> Segments, SegmentHold? Hold) HoldNewest(int epoch, double seconds)
    {
        lock (_lock)
        {
            var result = new List<SegmentInfo>();
            double acc = 0;
            foreach (var s in _segments.Values.Reverse())
            {
                if (s.Epoch != epoch) continue;
                result.Add(s);
                acc += s.Duration;
                if (acc >= seconds - 0.001) break;
            }
            result.Reverse();
            if (result.Count == 0) return (result, null);
            var hold = new SegmentHold(result[0].Index, result[^1].Index);
            _holds.Add(hold);
            return (result, hold);
        }
    }

    public SegmentHold Hold(int from, int to)
    {
        var h = new SegmentHold(from, to);
        lock (_lock) _holds.Add(h);
        return h;
    }

    public void Release(SegmentHold hold) { lock (_lock) _holds.Remove(hold); }

    public bool IsHeld(int index) { lock (_lock) return _holds.Any(h => h.Covers(index)); }

    public List<SegmentInfo> TakeExpired(int currentEpoch, double keepSeconds)
    {
        lock (_lock)
        {
            var currentCount = _segments.Values.Count(s => s.Epoch == currentEpoch);
            var expired = new List<SegmentInfo>();
            double acc = 0;
            foreach (var s in _segments.Values.Reverse())
            {
                bool drop;
                if (s.Epoch == currentEpoch)
                {
                    drop = acc >= keepSeconds;
                    acc += s.Duration;
                }
                else
                {
                    drop = currentCount >= 2 || (DateTime.UtcNow - s.CompletedAt).TotalSeconds > keepSeconds;
                }
                if (drop && !_holds.Any(h => h.Covers(s.Index))) expired.Add(s);
            }
            foreach (var s in expired) _segments.Remove(s.Index);
            return expired;
        }
    }

    public List<SegmentInfo> TakeUpTo(int lastIndex)
    {
        lock (_lock)
        {
            var taken = _segments.Values.Where(s => s.Index <= lastIndex && !_holds.Any(h => h.Covers(s.Index))).ToList();
            foreach (var s in taken) _segments.Remove(s.Index);
            return taken;
        }
    }

    public List<SegmentInfo> TakeAll()
    {
        lock (_lock)
        {
            var free = _segments.Values.Where(s => !_holds.Any(h => h.Covers(s.Index))).ToList();
            foreach (var s in free) _segments.Remove(s.Index);
            return free;
        }
    }

    public static bool TryParseCsvLine(string line, out string name, out double start, out double end)
    {
        name = ""; start = end = 0;
        var parts = line.Split(',');
        if (parts.Length < 3) return false;
        name = parts[0].Trim();
        return double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out start)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out end);
    }

    public static bool TryParseIndex(string fileName, out int index)
    {
        index = -1;
        var n = Path.GetFileNameWithoutExtension(fileName);
        if (!n.StartsWith("s_", StringComparison.Ordinal)) return false;
        return int.TryParse(n.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }
}

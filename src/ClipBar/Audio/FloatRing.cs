namespace ClipBar.Audio;

internal sealed class FloatRing
{
    public const int Channels = 2;

    readonly float[] _buf;
    readonly int _capacity;
    readonly object _gate = new();
    int _read, _count;

    public FloatRing(int capacityFrames)
    {
        if (capacityFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityFrames), capacityFrames, "Ring capacity must be positive.");
        _capacity = capacityFrames;
        _buf = new float[capacityFrames * Channels];
    }

    public int Count { get { lock (_gate) return _count; } }

    public long Overflowed { get; private set; }

    public long Truncated { get; private set; }

    public int Write(ReadOnlySpan<float> samples)
    {
        int frames = samples.Length / Channels;
        int truncated = 0, evicted = 0;
        lock (_gate)
        {
            if (frames > _capacity)
            {
                truncated += frames - _capacity;
                samples = samples[^(_capacity * Channels)..];
                frames = _capacity;
            }
            int over = _count + frames - _capacity;
            if (over > 0)
            {
                _read = (_read + over) % _capacity;
                _count -= over;
                evicted += over;
            }
            int w = (_read + _count) % _capacity;
            int first = Math.Min(frames, _capacity - w);
            samples[..(first * Channels)].CopyTo(_buf.AsSpan(w * Channels));
            if (first < frames)
                samples[(first * Channels)..].CopyTo(_buf.AsSpan(0));
            _count += frames;
            Overflowed += evicted;
            Truncated += truncated;
        }
        return truncated + evicted;
    }

    public int Read(Span<float> dst)
    {
        int frames = dst.Length / Channels;
        lock (_gate)
        {
            frames = Math.Min(frames, _count);
            int first = Math.Min(frames, _capacity - _read);
            _buf.AsSpan(_read * Channels, first * Channels).CopyTo(dst);
            if (first < frames)
                _buf.AsSpan(0, (frames - first) * Channels).CopyTo(dst[(first * Channels)..]);
            _read = (_read + frames) % _capacity;
            _count -= frames;
        }
        return frames;
    }

    public int Skip(int frames)
    {
        lock (_gate)
        {
            frames = Math.Clamp(frames, 0, _count);
            _read = (_read + frames) % _capacity;
            _count -= frames;
            return frames;
        }
    }

    public void Clear()
    {
        lock (_gate) { _read = 0; _count = 0; }
    }
}

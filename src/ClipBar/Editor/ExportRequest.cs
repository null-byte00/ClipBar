namespace ClipBar.Editor;

public enum TextPosition { Bottom, Center, Top }

public sealed record MediaSegment(string Path, TimeSpan In, TimeSpan Out, bool HasAudio);

public sealed class ExportRequest
{
    public required string SourcePath { get; init; }
    public required ClipMediaInfo Info { get; init; }
    public required ExportPresetKind Preset { get; init; }
    public TimeSpan In { get; init; }
    public TimeSpan Out { get; init; }
    public required string Title { get; init; }
    public string? OutputFolder { get; init; }

    public float SystemGain { get; init; } = 1f;
    public float MicGain { get; init; } = 1f;
    public float MasterGain { get; init; } = 1f;

    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 4.0;

    public double Speed { get; init; } = 1.0;

    public double Brightness { get; init; }
    public double Contrast { get; init; } = 1.0;
    public double Saturation { get; init; } = 1.0;
    public bool Mirror { get; init; }
    public int Rotation { get; init; }
    public TimeSpan FadeIn { get; init; }
    public TimeSpan FadeOut { get; init; }

    public string? Text { get; init; }
    public TextPosition TextPosition { get; init; } = TextPosition.Bottom;
    public double TextScale { get; init; } = 1.0;

    internal string? TextFilter { get; set; }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public IReadOnlyList<(TimeSpan In, TimeSpan Out)>? Segments { get; init; }

    public bool MultiSegment => Segments is { Count: > 1 };

    public IReadOnlyList<MediaSegment>? MediaSegments { get; init; }

    public bool MultiMedia => MediaSegments is { Count: > 1 };

    public bool AnyMulti => MultiSegment || MultiMedia;

    public int SegmentCount => MultiMedia ? MediaSegments!.Count : MultiSegment ? Segments!.Count : 1;

    IEnumerable<double> SegmentDurations()
    {
        if (MultiMedia) foreach (var s in MediaSegments!) yield return Math.Max(0, (s.Out - s.In).TotalSeconds);
        else foreach (var (a, b) in EffectiveSegments) yield return Math.Max(0, (b - a).TotalSeconds);
    }

    public TimeSpan Transition { get; init; }
    public string TransitionType { get; init; } = "fade";
    public bool HasTransition => AnyMulti && Transition > TimeSpan.Zero;

    public double EffectiveTransitionSeconds
    {
        get
        {
            if (!HasTransition) return 0;
            var minLen = double.MaxValue;
            foreach (var d in SegmentDurations()) minLen = Math.Min(minLen, d);
            var effT = Math.Min(Transition.TotalSeconds, minLen * 0.8);
            return effT > 0.05 ? effT : 0;
        }
    }

    public IReadOnlyList<(TimeSpan In, TimeSpan Out)> EffectiveSegments =>
        MultiSegment ? Segments! : [(In, Out)];

    double SumSegmentsSeconds()
    {
        double s = 0;
        foreach (var d in SegmentDurations()) s += d;
        return s;
    }

    public TimeSpan Length => Out - In;

    public bool AudioChanged => Info.HasAudio && (Info.HasSeparateTracks
        ? !Near(SystemGain, 1f) || !Near(MicGain, 1f)
        : !Near(MasterGain, 1f));

    public bool SpeedChanged => Math.Abs(Speed - 1.0) > 0.001;

    public bool ColorChanged =>
        Math.Abs(Brightness) > 0.001 || Math.Abs(Contrast - 1.0) > 0.001 || Math.Abs(Saturation - 1.0) > 0.001;

    public bool HasFades => FadeIn > TimeSpan.Zero || FadeOut > TimeSpan.Zero;

    public bool Rotated => Rotation % 360 != 0;

    bool SwapsAxes => ((Rotation % 360) + 360) % 360 is 90 or 270;
    public int EffWidth => SwapsAxes ? Info.Height : Info.Width;
    public int EffHeight => SwapsAxes ? Info.Width : Info.Height;

    public bool NeedsSoftwareFilters => ColorChanged || Mirror || HasFades || HasText || Rotated;

    public bool VideoChanged => SpeedChanged || NeedsSoftwareFilters;

    public TimeSpan OutputLength
    {
        get
        {
            var src = AnyMulti ? SumSegmentsSeconds() : Length.TotalSeconds;
            src -= EffectiveTransitionSeconds * (AnyMulti ? SegmentCount - 1 : 0);
            src = Math.Max(0.05, src);
            return TimeSpan.FromSeconds(src / (SpeedChanged ? Math.Clamp(Speed, MinSpeed, MaxSpeed) : 1.0));
        }
    }

    static bool Near(float a, float b) => Math.Abs(a - b) < 0.005f;
}

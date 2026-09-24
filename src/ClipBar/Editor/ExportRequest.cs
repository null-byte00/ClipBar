namespace ClipBar.Editor;

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

    public TimeSpan Length => Out - In;

    public bool AudioChanged => Info.HasAudio && (Info.HasSeparateTracks
        ? !Near(SystemGain, 1f) || !Near(MicGain, 1f)
        : !Near(MasterGain, 1f));

    static bool Near(float a, float b) => Math.Abs(a - b) < 0.005f;
}

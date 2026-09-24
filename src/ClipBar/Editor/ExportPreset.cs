namespace ClipBar.Editor;

public enum ExportPresetKind
{
    FastCopy,
    Precise,
    Discord,
    Vertical,
    Gif,
    Mp3,
}

public sealed record ExportPreset(ExportPresetKind Kind, string Title, string Subtitle, string Extension)
{
    public bool IsVideo => Extension == ".mp4";

    public static readonly IReadOnlyList<ExportPreset> All =
    [
        new(ExportPresetKind.FastCopy, "Быстро, без перекодирования", "Мгновенно · режет по ключевым кадрам (±1 с)", ".mp4"),
        new(ExportPresetKind.Precise,  "Точно по кадрам",             "Перекодирование на видеокарте · исходное качество", ".mp4"),
        new(ExportPresetKind.Discord,  "Для Discord (до 10 МБ)",      "Подбирает битрейт под лимит · до 720p", ".mp4"),
        new(ExportPresetKind.Vertical, "Вертикально 9:16",            "TikTok / Shorts / Reels · 1080×1920", ".mp4"),
        new(ExportPresetKind.Gif,      "GIF",                         "15 к/с · 480 px · без звука", ".gif"),
        new(ExportPresetKind.Mp3,      "Только звук (MP3)",           "Микс дорожек · 192 кбит/с", ".mp3"),
    ];

    public static ExportPreset Get(ExportPresetKind kind) => All.First(p => p.Kind == kind);
}

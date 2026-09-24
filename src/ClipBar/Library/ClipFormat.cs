using System.Globalization;
using ClipBar.Core;

namespace ClipBar.Library;

public static class ClipFormat
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Size(long bytes)
    {
        const double k = 1024;
        if (bytes >= k * k * k) return (bytes / (k * k * k)).ToString("0.0", Ru) + " ГБ";
        if (bytes >= k * k) return (bytes / (k * k)).ToString("0.0", Ru) + " МБ";
        if (bytes >= k) return Math.Round(bytes / k).ToString("0", Ru) + " КБ";
        return bytes.ToString(Ru) + " Б";
    }

    public static string Duration(TimeSpan? t)
    {
        if (t is null) return "";
        var s = (long)Math.Round(t.Value.TotalSeconds);
        return $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}";
    }

    public static string Date(DateTime d) =>
        d.Year == DateTime.Now.Year ? d.ToString("d MMMM", Ru) : d.ToString("d MMMM yyyy", Ru);

    public static string DateTimeLong(DateTime d) => d.ToString("d MMMM yyyy, HH:mm", Ru);

    public static string Meta(ClipInfo c) => $"{Date(c.CreatedAt)} · {Size(c.SizeBytes)}";

    public static string DayGroup(DateTime d)
    {
        var today = DateTime.Today;
        if (d.Date == today) return "Сегодня";
        if (d.Date == today.AddDays(-1)) return "Вчера";
        return Date(d);
    }

    public static string Resolution(int w, int h) => w > 0 && h > 0 ? $"{w} × {h}" : "";

    public static string AudioTracks(int n) => n switch
    {
        0 => "Без звука",
        1 => "1 звуковая дорожка",
        2 => "2 звуковые дорожки: микс и система",
        3 => "3 звуковые дорожки: микс, система, микрофон",
        _ => $"{n} звуковых дорожек",
    };

    public static string Plural(int n, string one, string few, string many)
    {
        var m10 = n % 10; var m100 = n % 100;
        var word = m10 == 1 && m100 != 11 ? one
            : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? few
            : many;
        return $"{n} {word}";
    }
}

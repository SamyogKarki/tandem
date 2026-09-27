using System.Globalization;

namespace Tandem.App;

internal static class Format
{
    public static string Bytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} B"
            : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string Date(DateTimeOffset when)
    {
        var local = when.LocalDateTime;
        var today = DateTime.Today;
        if (local.Date == today) return "Today, " + local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today.AddDays(-1)) return "Yesterday, " + local.ToString("t", CultureInfo.CurrentCulture);
        return local.ToString("g", CultureInfo.CurrentCulture);
    }
}

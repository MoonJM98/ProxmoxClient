using System.Globalization;
using ProxmoxClient.Mobile.Localization;

namespace ProxmoxClient.Mobile.Services;

/// <summary>숫자 표시 형식(용량·비율·가동 시간).</summary>
public static class Format
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "0 B";

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : value.ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00", CultureInfo.CurrentCulture) + " " +
              Units[unit];
    }

    public static string UsedOf(long used, long total)
    {
        return total > 0 ? $"{Bytes(used)} / {Bytes(total)}" : Bytes(used);
    }

    public static double Ratio(long used, long total)
    {
        return total > 0 ? Math.Clamp(100.0 * used / total, 0, 100) : 0;
    }

    public static string Percent(double percent)
    {
        return percent.ToString(percent >= 10 ? "0" : "0.0", CultureInfo.CurrentCulture) + "%";
    }

    public static string Uptime(long seconds)
    {
        if (seconds <= 0) return "-";

        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1) return L.T("Format_UptimeDays", (int)span.TotalDays, span.Hours);
        if (span.TotalHours >= 1) return L.T("Format_UptimeHours", span.Hours, span.Minutes);
        return L.T("Format_UptimeMinutes", Math.Max(1, span.Minutes));
    }

    public static string LocalTime(DateTime utc)
    {
        if (utc == default) return "-";

        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("T", CultureInfo.CurrentCulture)
            : local.ToString("g", CultureInfo.CurrentCulture);
    }
}

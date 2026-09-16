using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Vpn;

namespace ProxmoxClient.App;

/// <summary>태그 문자열을 팔레트 색으로 변환 — 같은 태그는 항상 같은 색.</summary>
public sealed class TagToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush[] Palette =
    [
        Create(0x4F, 0x8C, 0xFF),
        Create(0x3F, 0xB9, 0x6E),
        Create(0xE5, 0x70, 0x00),
        Create(0x9C, 0x6B, 0xDE),
        Create(0xD9, 0x5A, 0x5A),
        Create(0x2F, 0xA8, 0xA0),
        Create(0xC9, 0x9E, 0x3F),
        Create(0x6E, 0x8B, 0x45)
    ];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tag = value as string ?? string.Empty;
        var hash = 0;
        foreach (var ch in tag) hash = hash * 31 + ch;

        var brush = Palette[Math.Abs(hash) % Palette.Length];
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static SolidColorBrush Create(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(
            Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>bool → Visibility 변환.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public sealed class BytesHumanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bytes) return "-";

        return FormatBytes(bytes);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }

    /// <summary>공통 바이트 형식(#,##0.## + 설정 단위) — <see cref="ByteFormatter" /> 에 위임.</summary>
    public static string FormatBytes(long bytes)
    {
        return ByteFormatter.Format(bytes);
    }
}

public sealed class UptimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long seconds || seconds <= 0) return "-";

        var t = TimeSpan.FromSeconds(seconds);
        return t.Days > 0
            ? Loc.T("Uptime_WithDays", t.Days, t.Hours, t.Minutes, t.Seconds)
            : $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public sealed class DateTimeLocalConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime time || time == default) return "-";

        return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", culture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public sealed class KindLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is ResourceKind kind ? kind.Label() : "-";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value?.ToString();
        return name is not null && name.Equals(parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null)
        {
            if (targetType == typeof(string)) return parameter.ToString();

            if (targetType.IsEnum) return Enum.Parse(targetType, parameter.ToString()!, true);
        }

        return Binding.DoNothing;
    }
}

public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
    private static readonly Brush Err = new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));

    static StatusToBrushConverter()
    {
        Ok.Freeze();
        Warn.Freeze();
        Err.Freeze();
        Dim.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value?.ToString() ?? string.Empty;

        if (parameter?.ToString() == "task")
        {
            if (status.Equals("running", StringComparison.OrdinalIgnoreCase)) return Warn;

            return status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? Ok : Err;
        }

        if (status.Equals("running", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("online", StringComparison.OrdinalIgnoreCase))
            return Ok;

        if (status.Equals("paused", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return Warn;

        return Dim;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public sealed class VpnStateToBrushConverter : IValueConverter
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
    private static readonly Brush Err = new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));

    static VpnStateToBrushConverter()
    {
        Ok.Freeze();
        Warn.Freeze();
        Err.Freeze();
        Dim.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            VpnState.Connected => Ok,
            VpnState.Connecting or VpnState.Reconnecting or VpnState.Disconnecting => Warn,
            VpnState.Error => Err,
            _ => Dim
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
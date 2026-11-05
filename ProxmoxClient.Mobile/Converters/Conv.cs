using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Mobile.Services;

namespace ProxmoxClient.Mobile.Converters;

/// <summary>XAML 에서 <c>{x:Static c:Conv.Bytes}</c> 로 쓰는 표시 변환기 모음.</summary>
public static class Conv
{
    public static readonly IBrush OkBrush = new ImmutableSolidColorBrush(Color.Parse("#3BA55C"));
    public static readonly IBrush WarnBrush = new ImmutableSolidColorBrush(Color.Parse("#E0A100"));
    public static readonly IBrush ErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#E5534B"));
    public static readonly IBrush NeutralBrush = new ImmutableSolidColorBrush(Color.Parse("#8A8A8A"));
    public static readonly IBrush RunningBrush = new ImmutableSolidColorBrush(Color.Parse("#1E88E5"));

    public static readonly IValueConverter Bytes = new FuncValueConverter<long, string>(Format.Bytes);

    public static readonly IValueConverter Percent = new FuncValueConverter<double, string>(Format.Percent);

    public static readonly IValueConverter Uptime = new FuncValueConverter<long, string>(Format.Uptime);

    public static readonly IValueConverter LocalTime = new FuncValueConverter<DateTime, string>(Format.LocalTime);

    public static readonly IValueConverter KindLabel =
        new FuncValueConverter<ResourceKind, string>(kind => kind.Label());

    /// <summary>(사용량, 최대) → 0~100.</summary>
    public static readonly IMultiValueConverter Ratio = new FuncMultiValueConverter<long, double>(values =>
    {
        var list = values.ToList();
        return list.Count == 2 ? Format.Ratio(list[0], list[1]) : 0;
    });

    /// <summary>(사용량, 최대) → "1.2 GiB / 4.0 GiB".</summary>
    public static readonly IMultiValueConverter UsedOf = new FuncMultiValueConverter<long, string>(values =>
    {
        var list = values.ToList();
        return list.Count == 2 ? Format.UsedOf(list[0], list[1]) : string.Empty;
    });

    /// <summary>게스트·노드·스토리지 상태 문자열 → 표시 색.</summary>
    public static readonly IValueConverter StatusBrush = new FuncValueConverter<string?, IBrush>(StatusToBrush);

    /// <summary>작업 상태 → 표시 색(진행 중 파랑, 성공 초록, 그 외 빨강).</summary>
    public static readonly IValueConverter TaskBrush = new FuncValueConverter<string?, IBrush>(status =>
        string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) ? RunningBrush
        : string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) ? OkBrush
        : status?.StartsWith("WARNINGS", StringComparison.OrdinalIgnoreCase) == true ? WarnBrush
        : ErrorBrush);

    public static readonly IValueConverter TaskTitle =
        new FuncValueConverter<PveTask?, string>(task => task is null ? string.Empty : TaskTypes.Describe(task));

    public static IBrush StatusToBrush(string? status)
    {
        return status?.ToLowerInvariant() switch
        {
            "running" or "online" or "available" or "active" or "ok" => OkBrush,
            "paused" or "suspended" or "prelaunch" => WarnBrush,
            "stopped" or "offline" or "unknown" or "" or null => NeutralBrush,
            _ => ErrorBrush
        };
    }
}

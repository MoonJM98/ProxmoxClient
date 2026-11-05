using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Main;

/// <summary>
///     WPF 메인 창(Converters.cs)과 같은 색·같은 글자 규칙의 변환기. XAML 에서 <c>{x:Static vm:MainConv.StatusBrush}</c> 로 쓴다.
///     색은 WPF 다크 테마 값(BrushOk·BrushWarn·BrushErr·BrushDim)과 같다.
/// </summary>
public static class MainConv
{
    private static readonly IBrush Ok = Solid("#4EC9B0");
    private static readonly IBrush Warn = Solid("#DCDCAA");
    private static readonly IBrush Err = Solid("#F48771");
    private static readonly IBrush Dim = Solid("#9E9E9E");

    private static readonly IBrush[] TagPalette =
    [
        Solid("#4F8CFF"), Solid("#3FB96E"), Solid("#E57000"), Solid("#9C6BDE"),
        Solid("#D95A5A"), Solid("#2FA8A0"), Solid("#C99E3F"), Solid("#6E8B45")
    ];

    /// <summary>게스트·노드·스토리지 상태 → 점 색(WPF StatusToBrushConverter).</summary>
    public static readonly IValueConverter StatusBrush = new FuncValueConverter<string?, IBrush>(status =>
        status is "running" or "online" ? Ok
        : status is "paused" or "unknown" ? Warn
        : Dim);

    /// <summary>작업 상태 → 색(WPF StatusToBrushConverter ConverterParameter=task).</summary>
    public static readonly IValueConverter TaskBrush = new FuncValueConverter<string?, IBrush>(status =>
        string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) ? Warn
        : string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) ? Ok
        : Err);

    /// <summary>태그 문자열 → 팔레트 색(같은 태그는 항상 같은 색 — WPF TagToBrushConverter 와 같은 팔레트).</summary>
    public static readonly IValueConverter TagBrush = new FuncValueConverter<string?, IBrush>(tag =>
    {
        var hash = 0;
        foreach (var ch in tag ?? string.Empty) hash = hash * 31 + ch;
        return TagPalette[Math.Abs(hash) % TagPalette.Length];
    });

    /// <summary>게스트 종류 → 아이콘(VM 모니터 · CT 상자).</summary>
    public static readonly IValueConverter KindIcon = new FuncValueConverter<ResourceKind, Geometry?>(kind =>
        ResourceGeometry(kind == ResourceKind.Lxc ? "IconBox" : "IconMonitor"));

    public static readonly IValueConverter KindLabel =
        new FuncValueConverter<ResourceKind, string>(kind => kind.Label());

    /// <summary>바이트 → 사람이 읽는 값(설정한 단위 — WPF BytesHumanConverter).</summary>
    public static readonly IValueConverter Bytes =
        new FuncValueConverter<long, string>(bytes => ByteFormatter.Format(bytes));

    /// <summary>업타임 → 기간 문구(0 이하는 "-" — WPF UptimeConverter).</summary>
    public static readonly IValueConverter Uptime = new FuncValueConverter<long, string>(seconds =>
        seconds > 0 ? TableFormats.FormatUptime(seconds) : "-");

    /// <summary>UTC 시각 → 로컬 "yyyy-MM-dd HH:mm:ss"(없으면 "-" — WPF DateTimeLocalConverter).</summary>
    public static readonly IValueConverter LocalTime = new FuncValueConverter<DateTime?, string>(time =>
        time is { } utc && utc != default
            ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
            : "-");

    private static Geometry? ResourceGeometry(string key)
    {
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var value)
            ? value as Geometry
            : null;
    }

    private static IBrush Solid(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}

using System.Globalization;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api.Versioning;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>표의 한 열 — 서버 필드 이름, 머리글 리소스 키, 표시 형식.</summary>
public sealed class TableColumn
{
    public required string Key { get; init; }

    public required string HeaderKey { get; init; }

    /// <summary>0 이하이면 남은 폭을 채운다.</summary>
    public double Width { get; init; } = 120;

    /// <summary>원래 문자열을 화면용으로 바꾼다. 없으면 그대로 보인다.</summary>
    public Func<string, string>? Format { get; init; }
}

/// <summary>표 위에 놓이는 작업 버튼.</summary>
public sealed class TableAction
{
    public required string LabelKey { get; init; }

    public required string IconKey { get; init; }

    /// <summary>
    ///     선택한 행(없으면 null)과 대화상자의 주인 창을 받아 실행한다.
    ///     결과 문구를 돌려주며, 사용자가 취소했으면 null 이다.
    /// </summary>
    public required Func<IReadOnlyDictionary<string, string>?, Window?, Task<string?>> Run { get; init; }

    public bool NeedsSelection { get; init; }

    /// <summary>실행 전에 물어볼 문구를 선택한 행으로 만든다. 없으면 바로 실행한다.</summary>
    public Func<IReadOnlyDictionary<string, string>?, string>? Confirm { get; init; }

    /// <summary>이 버튼이 쓰는 API 기능 — 서버가 못 쓰면 버튼을 두지 않는다(표·옵션 화면이 알아서 뺀다).</summary>
    public ApiFeature? Requires { get; init; }
}

/// <summary>표에 보여 줄 한 행 — 없는 필드는 빈칸, 형식은 미리 적용해 둔다.</summary>
public sealed class TableRow
{
    private readonly Dictionary<string, string> _display;

    public TableRow(IReadOnlyDictionary<string, string> source, IReadOnlyList<TableColumn> columns)
    {
        Source = source;
        _display = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            var raw = source.TryGetValue(column.Key, out var value) ? value : string.Empty;
            _display[column.Key] = column.Format is { } format ? format(raw) : raw;
        }
    }

    public IReadOnlyDictionary<string, string> Source { get; }

    public string this[string key] => _display.TryGetValue(key, out var value) ? value : string.Empty;
}

/// <summary>자주 쓰는 열 형식.</summary>
public static class TableFormats
{
    public static string Bytes(string raw)
    {
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var bytes)
            ? ByteFormatter.Format(bytes)
            : raw;
    }

    /// <summary>유닉스 시각(초) → 현지 날짜·시간.</summary>
    public static string EpochDate(string raw)
    {
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("g", CultureInfo.CurrentCulture)
            : raw;
    }

    /// <summary>0~1 비율 → "12.3 %" (값이 없으면 빈칸).</summary>
    public static string Percent(string raw)
    {
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio)
            ? (ratio * 100).ToString("0.0", CultureInfo.CurrentCulture) + " %"
            : string.Empty;
    }

    /// <summary>가동 시간(초) → "3일 04:05:06" / "04:05:06" (0 이하면 빈칸).</summary>
    public static string Uptime(string raw)
    {
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? FormatUptime(seconds)
            : string.Empty;
    }

    public static string FormatUptime(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.Days > 0
            ? Loc.T("Uptime_WithDays", t.Days, t.Hours, t.Minutes, t.Seconds)
            : $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
    }

    /// <summary>1/0 → ✓/빈칸.</summary>
    public static string Flag(string raw)
    {
        return raw is "1" or "true" ? "✓" : string.Empty;
    }

    /// <summary>1/0 을 반대로 — "disable" 같은 필드를 '사용' 열로 보일 때.</summary>
    public static string InverseFlag(string raw)
    {
        return raw is "1" or "true" ? string.Empty : "✓";
    }
}

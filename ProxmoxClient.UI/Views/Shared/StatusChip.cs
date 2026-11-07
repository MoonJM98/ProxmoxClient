using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ProxmoxClient.UI.Views.Shared;

/// <summary>
///     상태 칩 — 목록 카드·상세 시트 오른쪽 위의 작은 색 딱지. 글자로 좋음(초록)·나쁨(빨강)·주의(노랑)·진행(파랑)을 가른다.
///     긴 문구(작업 오류 등)는 칩에서는 자르고, 전문은 상세 시트에 나온다.
/// </summary>
public static class StatusChip
{
    public enum Tone
    {
        Neutral,
        Ok,
        Warn,
        Error,
        Busy
    }

    private const int MaxChipChars = 18;

    public static Tone Classify(string text)
    {
        var value = text.Trim().ToLowerInvariant();
        if (value.Length == 0) return Tone.Neutral;
        if (value.StartsWith("error", StringComparison.Ordinal) || value.Contains("fail", StringComparison.Ordinal) ||
            value is "down" or "dead" or "offline" or "health_err" or "error" or "disconnected" or "unknown" or "fence")
            return Tone.Error;
        if (value.StartsWith("warning", StringComparison.Ordinal) ||
            value is "health_warn" or "degraded" or "pending" or "stale" or "suspended" or "paused" or "expired" or "notfound" or "invalid")
            return Tone.Warn;
        if (value is "running" or "starting" or "migrating" or "syncing" or "recovery" or "relocate" or "queued")
            return Tone.Busy;
        if (value is "ok" or "active" or "online" or "available" or "up" or "started" or "health_ok" or "enabled" or "in" or "true" or "yes" or "1" or "✓" or "connected" or "healthy" or "new")
            return Tone.Ok;
        return Tone.Neutral;
    }

    public static Border Create(string text, bool full = false, Tone? tone = null)
    {
        var shade = tone ?? Classify(text);
        var color = shade switch
        {
            Tone.Ok => Color.Parse("#4EC9B0"),
            Tone.Warn => Color.Parse("#DCDCAA"),
            Tone.Error => Color.Parse("#F48771"),
            Tone.Busy => Color.Parse("#3794FF"),
            _ => Color.Parse("#A0A0A0")
        };
        // "ERROR: 긴 설명" 은 칩에 앞말만(전문은 상세 시트)
        var colon = text.IndexOf(':');
        var shown = full ? text
            : colon is > 0 and <= MaxChipChars && text.Length > MaxChipChars ? text[..colon]
            : text.Length > MaxChipChars ? text[..(MaxChipChars - 1)] + "…"
            : text;
        return new Border
        {
            Background = new SolidColorBrush(color, 0.16),
            BorderBrush = new SolidColorBrush(color, 0.45),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock
            {
                Text = shown, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(color),
                TextWrapping = full ? TextWrapping.Wrap : TextWrapping.NoWrap
            }
        };
    }
}

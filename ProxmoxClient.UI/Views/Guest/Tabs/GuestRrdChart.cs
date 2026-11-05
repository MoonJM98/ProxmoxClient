using Avalonia.Media;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Views.Guest.Tabs;

/// <summary>
///     게스트 rrddata 표본 → 통합 그래프 시리즈(WPF MainViewModel.Graphs 의 BuildGraph 와 같은 규칙).
///     CPU·메모리 = 왼쪽 % 축, 네트워크·디스크 IO = 오른쪽 초당 바이트 축.
/// </summary>
internal static class GuestRrdChart
{
    private static readonly Color CpuColor = Color.FromRgb(0x4F, 0x8C, 0xFF);
    private static readonly Color MemoryColor = Color.FromRgb(0x3F, 0xB9, 0x6E);
    private static readonly Color NetworkColor = Color.FromRgb(0xF0, 0x9A, 0x3E);
    private static readonly Color DiskIoColor = Color.FromRgb(0xB0, 0x7C, 0xF0);

    internal sealed record Chart(
        IReadOnlyList<GraphSeries> Series,
        IReadOnlyList<string> XLabels,
        IReadOnlyList<DateTime?> Times);

    public static Chart Build(string timeframe, IReadOnlyList<RrdSample> samples)
    {
        IReadOnlyList<GraphSeries> series =
        [
            new("CPU",
                samples.Select(s => s.Cpu is { } cpu ? 100.0 * cpu : (double?)null).ToList(),
                CpuColor, GraphAxis.Left, GraphValueUnit.Percent),
            new(Loc.T("MainWindow_19"),
                samples.Select(s =>
                        s.Mem is { } mem && s.MaxMem is { } maxMem && maxMem > 0 ? 100.0 * mem / maxMem : (double?)null)
                    .ToList(),
                MemoryColor, GraphAxis.Left, GraphValueUnit.Percent),
            new(Loc.T("Graph_Network"),
                samples.Select(s => SumOrNull(s.NetIn, s.NetOut)).ToList(),
                NetworkColor, GraphAxis.Right, GraphValueUnit.BytesPerSecond),
            new(Loc.T("Graph_DiskIo"),
                samples.Select(s => SumOrNull(s.DiskRead, s.DiskWrite)).ToList(),
                DiskIoColor, GraphAxis.Right, GraphValueUnit.BytesPerSecond)
        ];

        IReadOnlyList<DateTime?> times = samples
            .Select(s => s.TimeUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(s.TimeUnix).LocalDateTime : (DateTime?)null)
            .ToList();

        var unix = samples.Select(s => s.TimeUnix).Where(t => t > 0).ToList();
        var xLabels = new List<string>();
        if (unix.Count >= 2)
        {
            xLabels.Add(AxisLabel(unix[0], timeframe));
            xLabels.Add(AxisLabel(unix[unix.Count / 2], timeframe));
            xLabels.Add(AxisLabel(unix[^1], timeframe));
        }

        return new Chart(series, xLabels, times);
    }

    /// <summary>한쪽만 값이 있어도 합계(null + 값 = null 로 선이 끊기던 문제 방지). 둘 다 없으면 null.</summary>
    private static double? SumOrNull(double? first, double? second)
    {
        return first is null && second is null ? null : (first ?? 0) + (second ?? 0);
    }

    private static string AxisLabel(long unixSeconds, string timeframe)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
        return timeframe switch
        {
            "hour" or "day" => time.ToString("HH:mm"),
            "year" => time.ToString("yy/MM"),
            _ => time.ToString("MM/dd")
        };
    }
}

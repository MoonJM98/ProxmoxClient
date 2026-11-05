using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Views.Main;

/// <summary>노드·게스트 사용량 그래프 — 불러오기·시리즈 만들기·축 표시(WPF MainViewModel.Graphs 와 같다).</summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] public partial IReadOnlyList<GraphSeries>? GuestGraphSeries { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<string>? GuestGraphXLabels { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<DateTime?>? GuestGraphTimes { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<GraphSeries>? NodeGraphSeries { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<string>? NodeGraphXLabels { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<DateTime?>? NodeGraphTimes { get; private set; }

    /// <summary>게스트 그래프 기간(hour·day·week·month·year) — WPF GuestTfBox 의 선택.</summary>
    [ObservableProperty] public partial string GuestTimeframe { get; set; } = "hour";

    [ObservableProperty] public partial string NodeTimeframe { get; set; } = "hour";

    private long _guestGraphLoadedAt;
    private long _nodeGraphLoadedAt;
    private string? _guestGraphSignature;
    private string? _nodeGraphSignature;

    // 선택 변경과 틱이 겹칠 때 늦게 도착한 이전 요청이 새 그래프를 덮어쓰지 않도록 요청 버전으로 거른다
    private int _guestGraphVersion;
    private int _nodeGraphVersion;

    public TimeframeOption? GuestTimeframeOption
    {
        get => Timeframes.FirstOrDefault(t => t.Key == GuestTimeframe);
        set
        {
            if (value is { } option && option.Key != GuestTimeframe) GuestTimeframe = option.Key;
        }
    }

    public TimeframeOption? NodeTimeframeOption
    {
        get => Timeframes.FirstOrDefault(t => t.Key == NodeTimeframe);
        set
        {
            if (value is { } option && option.Key != NodeTimeframe) NodeTimeframe = option.Key;
        }
    }

    partial void OnGuestTimeframeChanged(string value)
    {
        OnPropertyChanged(nameof(GuestTimeframeOption));
        _ = LoadGuestGraphAsync();
    }

    partial void OnNodeTimeframeChanged(string value)
    {
        OnPropertyChanged(nameof(NodeTimeframeOption));
        _ = LoadNodeGraphAsync();
    }

    /// <param name="force">false 면(주기 새로고침) 마지막 조회 후 <see cref="GraphRefreshInterval" /> 가 지나야 조회.</param>
    private async Task LoadGuestGraphAsync(bool force = true)
    {
        if (_disposed || Session.Api is not { } api || SelectedGuest is not { } guest)
        {
            _guestGraphVersion++;
            _guestGraphSignature = null;
            _guestGraphLoadedAt = 0;
            GuestGraphSeries = null;
            return;
        }

        if (!force && !IsGraphDue(_guestGraphLoadedAt)) return;

        _guestGraphLoadedAt = Environment.TickCount64;
        var version = ++_guestGraphVersion;
        var timeframe = GuestTimeframe;
        try
        {
            // rrddata JSON → 클라이언트 차트(rrddata 는 지원 범위 7~9 모두에 있다)
            var samples = await api.GetGuestRrdDataAsync(guest.Node, guest.Kind, guest.VmId, timeframe)
                .ConfigureAwait(true);
            if (version != _guestGraphVersion) return; // 더 새 요청(선택·기간 변경·연결 해제)이 있음

            // 시그니처가 같으면(새 표본 없음) 시리즈 목록을 만들지 않는다
            var signature = GraphSignature($"{guest.Node}:{guest.VmId}", timeframe, samples);
            if (_guestGraphSignature == signature) return;

            var graph = BuildGraph(timeframe, samples);
            _guestGraphSignature = signature;
            GuestGraphXLabels = graph.XLabels;
            GuestGraphTimes = graph.Times;
            GuestGraphSeries = graph.Series;
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            // 일시 오류 — 마지막 그래프를 유지하고 다음 주기에 재시도
        }
    }

    /// <param name="force">false 면(주기 새로고침) 마지막 조회 후 <see cref="GraphRefreshInterval" /> 가 지나야 조회.</param>
    private async Task LoadNodeGraphAsync(bool force = true)
    {
        if (_disposed || Session.Api is not { } api || SelectedNode is not { } node)
        {
            _nodeGraphVersion++;
            _nodeGraphSignature = null;
            _nodeGraphLoadedAt = 0;
            NodeGraphSeries = null;
            return;
        }

        if (!force && !IsGraphDue(_nodeGraphLoadedAt)) return;

        _nodeGraphLoadedAt = Environment.TickCount64;
        var version = ++_nodeGraphVersion;
        var timeframe = NodeTimeframe;
        try
        {
            var samples = await api.GetNodeRrdDataAsync(node.Node, timeframe).ConfigureAwait(true);
            if (version != _nodeGraphVersion) return;

            var signature = GraphSignature(node.Node, timeframe, samples);
            if (_nodeGraphSignature == signature) return;

            var graph = BuildGraph(timeframe, samples);
            _nodeGraphSignature = signature;
            NodeGraphXLabels = graph.XLabels;
            NodeGraphTimes = graph.Times;
            NodeGraphSeries = graph.Series;
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            // 일시 오류 — 다음 주기에 재시도
        }
    }

    private static bool IsGraphDue(long loadedAt)
    {
        return Environment.TickCount64 - loadedAt >= (long)GraphRefreshInterval.TotalMilliseconds;
    }

    /// <summary>표본 수·마지막 시각으로 새 데이터 여부 판단 — 같으면 시리즈를 다시 만들지 않는다.</summary>
    private static string GraphSignature(string ownerKey, string timeframe, IReadOnlyList<RrdSample> samples)
    {
        return $"{ownerKey}:{timeframe}:{samples.Count}:{(samples.Count > 0 ? samples[^1].TimeUnix : 0)}";
    }

    /// <summary>
    ///     rrddata 표본 → 통합 그래프(CPU·메모리 = 왼쪽 % 축, 네트워크·디스크 IO = 오른쪽 초당 바이트 축).
    ///     값이 없는 시리즈(예: 노드의 디스크 IO)는 그래프 컨트롤이 범례에서 제외한다.
    /// </summary>
    private static GraphData BuildGraph(string timeframe, IReadOnlyList<RrdSample> samples)
    {
        IReadOnlyList<GraphSeries> series =
        [
            new("CPU",
                samples.Select(s => s.Cpu is { } cpu ? 100.0 * cpu : (double?)null).ToList(),
                CpuSeriesColor, GraphAxis.Left, GraphValueUnit.Percent),
            new(L.T("MainWindow_19"),
                samples.Select(s =>
                        s.Mem is { } mem && s.MaxMem is { } maxMem && maxMem > 0 ? 100.0 * mem / maxMem : (double?)null)
                    .ToList(),
                MemorySeriesColor, GraphAxis.Left, GraphValueUnit.Percent),
            new(L.T("Graph_Network"),
                samples.Select(s => SumOrNull(s.NetIn, s.NetOut)).ToList(),
                NetworkSeriesColor, GraphAxis.Right, GraphValueUnit.BytesPerSecond),
            new(L.T("Graph_DiskIo"),
                samples.Select(s => SumOrNull(s.DiskRead, s.DiskWrite)).ToList(),
                DiskIoSeriesColor, GraphAxis.Right, GraphValueUnit.BytesPerSecond)
        ];

        IReadOnlyList<DateTime?> pointTimes = samples
            .Select(s => s.TimeUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(s.TimeUnix).LocalDateTime
                : (DateTime?)null)
            .ToList();

        var times = samples.Select(s => s.TimeUnix).Where(t => t > 0).ToList();
        IReadOnlyList<string> xLabels = times.Count >= 2
            ?
            [
                FormatTimeAxis(times[0], timeframe),
                FormatTimeAxis(times[times.Count / 2], timeframe),
                FormatTimeAxis(times[^1], timeframe)
            ]
            : [];

        return new GraphData(series, xLabels, pointTimes);
    }

    /// <summary>한쪽만 값이 있어도 합계 표시(null + 값 = null 로 선이 끊기던 문제 방지). 둘 다 없으면 null.</summary>
    private static double? SumOrNull(double? first, double? second)
    {
        return first is null && second is null ? null : (first ?? 0) + (second ?? 0);
    }

    private static string FormatTimeAxis(long unixSeconds, string timeframe)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
        return timeframe switch
        {
            "hour" or "day" => time.ToString("HH:mm", CultureInfo.CurrentCulture),
            "year" => time.ToString("yy/MM", CultureInfo.CurrentCulture),
            _ => time.ToString("MM/dd", CultureInfo.CurrentCulture)
        };
    }

    private sealed record GraphData(
        IReadOnlyList<GraphSeries> Series,
        IReadOnlyList<string> XLabels,
        IReadOnlyList<DateTime?> Times);
}

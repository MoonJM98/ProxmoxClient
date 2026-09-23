using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.Core.Settings;

namespace ProxmoxClient.App.ViewModels;

/// <summary>노드·게스트 사용량 그래프 — 불러오기·그리기·축 표시.</summary>
public partial class MainViewModel
{
    /// <param name="force">false 면(주기 새로고침) 마지막 조회 후 <see cref="GraphRefreshInterval" /> 가 지나야 조회.</param>
    private async Task LoadGuestGraphAsync(bool force = true)
    {
        if (Api is not { } api || SelectedGuest is not { } guest)
        {
            _guestGraphVersion++;
            _guestGraphSignature = null;
            _guestGraphLoadedAt = 0;
            GuestGraph = null;
            GuestGraphSeries = null;
            return;
        }

        if (!force && !IsGraphDue(_guestGraphLoadedAt)) return;

        _guestGraphLoadedAt = Environment.TickCount64;
        var version = ++_guestGraphVersion;
        var timeframe = GuestTimeframe;
        try
        {
            // rrddata JSON → 클라이언트 차트. 미지원 서버: rrdtool PNG 폴백.
            if (!_rrdDataUnsupported)
            {
                var samples = await api.GetGuestRrdDataAsync(guest.Node, guest.Kind, guest.VmId, timeframe)
                    .ConfigureAwait(true);
                if (version != _guestGraphVersion) return; // 더 새 요청(선택·기간 변경·연결 해제)이 있음

                // 시그니처가 같으면(새 표본 없음) 시리즈 목록을 만들지 않는다
                var signature = GraphSignature($"{guest.Node}:{guest.VmId}", timeframe, samples);
                if (_guestGraphSignature != signature)
                {
                    var graph = BuildGraph(timeframe, samples);
                    _guestGraphSignature = signature;
                    GuestGraphXLabels = graph.XLabels;
                    GuestGraphTimes = graph.Times;
                    GuestGraphSeries = graph.Series;
                }

                GuestGraph = null;
                return;
            }

            var png = await api.GetGuestRrdPngAsync(guest.Node, guest.Kind, guest.VmId, timeframe, GuestGraphDs)
                .ConfigureAwait(true);
            if (version == _guestGraphVersion)
            {
                GuestGraph = LoadPng(png);
                GuestGraphSeries = null;
                _guestGraphSignature = null;
            }
        }
        catch (ProxmoxApiException ex) when (!_rrdDataUnsupported && IsRrdDataUnsupported(ex))
        {
            _rrdDataUnsupported = true;
            if (version == _guestGraphVersion)
                await LoadGuestGraphAsync().ConfigureAwait(true); // 곧바로 PNG 경로로 한 번 더(플래그로 재귀 1회 한정)
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            // 일시 오류 — 마지막 그래프를 유지하고 다음 주기에 재시도
        }
    }
    /// <param name="force">false 면(주기 새로고침) 마지막 조회 후 <see cref="GraphRefreshInterval" /> 가 지나야 조회.</param>
    private async Task LoadNodeGraphAsync(bool force = true)
    {
        if (Api is not { } api || SelectedNode is not { } node)
        {
            _nodeGraphVersion++;
            _nodeGraphSignature = null;
            _nodeGraphLoadedAt = 0;
            NodeGraph = null;
            NodeGraphSeries = null;
            return;
        }

        if (!force && !IsGraphDue(_nodeGraphLoadedAt)) return;

        _nodeGraphLoadedAt = Environment.TickCount64;
        var version = ++_nodeGraphVersion;
        var timeframe = NodeTimeframe;
        try
        {
            if (!_rrdDataUnsupported)
            {
                var samples = await api.GetNodeRrdDataAsync(node.Node, timeframe).ConfigureAwait(true);
                if (version != _nodeGraphVersion) return;

                var signature = GraphSignature(node.Node, timeframe, samples);
                if (_nodeGraphSignature != signature)
                {
                    var graph = BuildGraph(timeframe, samples);
                    _nodeGraphSignature = signature;
                    NodeGraphXLabels = graph.XLabels;
                    NodeGraphTimes = graph.Times;
                    NodeGraphSeries = graph.Series;
                }

                NodeGraph = null;
                return;
            }

            var png = await api.GetNodeRrdPngAsync(node.Node, timeframe, NodeGraphDs).ConfigureAwait(true);
            if (version == _nodeGraphVersion)
            {
                NodeGraph = LoadPng(png);
                NodeGraphSeries = null;
                _nodeGraphSignature = null;
            }
        }
        catch (ProxmoxApiException ex) when (!_rrdDataUnsupported && IsRrdDataUnsupported(ex))
        {
            _rrdDataUnsupported = true;
            if (version == _nodeGraphVersion) await LoadNodeGraphAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
        }
    }
    private static bool IsGraphDue(long loadedAt)
    {
        return Environment.TickCount64 - loadedAt >= (long)GraphRefreshInterval.TotalMilliseconds;
    }
    /// <summary>rrddata 엔드포인트가 없는 구버전 서버 응답(권한·일시 오류는 제외).</summary>
    private static bool IsRrdDataUnsupported(ProxmoxApiException ex)
    {
        return ex.StatusCode is 400 or 404 or 501;
    }
    /// <summary>새로고침·조회 경로에서 삼키고 다음 주기에 재시도할 오류(연결 끊김·타임아웃·연결 해제 중 Dispose 등).</summary>
    private static bool IsTransientError(Exception ex)
    {
        return ex is ProxmoxApiException or CertificateTrustException or HttpRequestException or IOException
            or OperationCanceledException or ObjectDisposedException;
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
            new(Loc.T("MainWindow_19"),
                samples.Select(s =>
                        s.Mem is { } mem && s.MaxMem is { } maxMem && maxMem > 0 ? 100.0 * mem / maxMem : (double?)null)
                    .ToList(),
                MemorySeriesColor, GraphAxis.Left, GraphValueUnit.Percent),
            new(Loc.T("Graph_Network"),
                samples.Select(s => SumOrNull(s.NetIn, s.NetOut)).ToList(),
                NetworkSeriesColor, GraphAxis.Right, GraphValueUnit.BytesPerSecond),
            new(Loc.T("Graph_DiskIo"),
                samples.Select(s => SumOrNull(s.DiskRead, s.DiskWrite)).ToList(),
                DiskIoSeriesColor, GraphAxis.Right, GraphValueUnit.BytesPerSecond)
        ];

        IReadOnlyList<DateTime?> pointTimes = samples
            .Select(s =>
                s.TimeUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(s.TimeUnix).LocalDateTime : (DateTime?)null)
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
            "hour" or "day" => time.ToString("HH:mm"),
            "year" => time.ToString("yy/MM"),
            _ => time.ToString("MM/dd")
        };
    }
    private static ImageSource? LoadPng(byte[] png)
    {
        if (png.Length == 0) return null;

        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
    private sealed record GraphData(
        IReadOnlyList<GraphSeries> Series,
        IReadOnlyList<string> XLabels,
        IReadOnlyList<DateTime?> Times);
}

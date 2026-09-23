using System.Globalization;
using System.Text.Json;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Api;

/// <summary>노드·게스트 사용량 그래프(RRD) — PNG 와 수치.</summary>
public sealed partial class ProxmoxApiClient
{
    /// <summary>
    ///     Fetches a node RRD graph as PNG bytes
    ///     (GET /nodes/{node}/rrdtool?cf=AVERAGE&amp;timeframe={tf}&amp;ds={ds}).
    /// </summary>
    public Task<byte[]> GetNodeRrdPngAsync(string node, string timeframe = "hour", string ds = "cpu",
        CancellationToken ct = default)
    {
        return GetPngAsync($"nodes/{Escape(node)}/rrdtool?cf=AVERAGE&timeframe={Escape(timeframe)}&ds={Escape(ds)}",
            ct);
    }
    /// <summary>
    ///     Fetches a guest (VM/CT) RRD graph as PNG bytes
    ///     (GET /nodes/{node}/{qemu|lxc}/{vmid}/rrdtool?...).
    /// </summary>
    public Task<byte[]> GetGuestRrdPngAsync(string node, ResourceKind kind, int vmid, string timeframe = "hour",
        string ds = "cpu", CancellationToken ct = default)
    {
        return GetPngAsync(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/rrdtool"
            + $"?cf=AVERAGE&timeframe={Escape(timeframe)}&ds={Escape(ds)}",
            ct);
    }
    /// <summary>
    ///     게스트 시계열 데이터(GET .../rrddata?timeframe=hour|day|week|month|year).
    ///     PVE 8.2+에서 rrdtool PNG 엔드포인트가 제거되어 JSON 데이터 기반 렌더링에 사용.
    /// </summary>
    public async Task<IReadOnlyList<RrdSample>> GetGuestRrdDataAsync(
        string node, ResourceKind kind, int vmid, string timeframe = "hour", CancellationToken ct = default)
    {
        var data = await GetJsonAsync(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/rrddata"
            + $"?timeframe={Uri.EscapeDataString(timeframe)}&cf=AVERAGE",
            ct).ConfigureAwait(false);
        return ParseRrdSamples(data);
    }
    /// <summary>노드 시계열 데이터(GET nodes/{node}/rrddata).</summary>
    public async Task<IReadOnlyList<RrdSample>> GetNodeRrdDataAsync(
        string node, string timeframe = "hour", CancellationToken ct = default)
    {
        var data = await GetJsonAsync(
            $"nodes/{Escape(node)}/rrddata?timeframe={Uri.EscapeDataString(timeframe)}&cf=AVERAGE",
            ct).ConfigureAwait(false);
        return ParseRrdSamples(data);
    }
    private static List<RrdSample> ParseRrdSamples(JsonElement data)
    {
        var samples = new List<RrdSample>();
        if (data.ValueKind != JsonValueKind.Array) return samples;

        foreach (var item in data.EnumerateArray())
            samples.Add(new RrdSample
            {
                TimeUnix = GetLong(item, "time"),
                Cpu = GetDoubleOrNull(item, "cpu"),
                Mem = GetDoubleOrNull(item, "mem"),
                MaxMem = GetDoubleOrNull(item, "maxmem"),
                NetIn = GetDoubleOrNull(item, "netin"),
                NetOut = GetDoubleOrNull(item, "netout"),
                DiskRead = GetDoubleOrNull(item, "diskread"),
                DiskWrite = GetDoubleOrNull(item, "diskwrite")
            });

        return samples;
    }
    private static double? GetDoubleOrNull(in JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var el)
               && el.ValueKind is JsonValueKind.Number or JsonValueKind.String
               && double.TryParse(el.ToString(), CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}

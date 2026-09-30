using System.Globalization;
using System.Text.Json;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Api;

/// <summary>노드·게스트 사용량 그래프(RRD) 수치.</summary>
public sealed partial class ProxmoxApiClient
{
    /// <summary>
    ///     게스트 시계열 데이터(GET .../rrddata?timeframe=hour|day|week|month|year) — 앱이 직접 그래프로 그린다.
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/qemu/{vmid}/rrddata")]
    [Versioning.PveApi("GET", "/nodes/{node}/lxc/{vmid}/rrddata")]
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
    [Versioning.PveApi("GET", "/nodes/{node}/rrddata")]
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

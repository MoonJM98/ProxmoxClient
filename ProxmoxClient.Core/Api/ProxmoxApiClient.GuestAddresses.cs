using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Api;

public sealed partial class ProxmoxApiClient
{
    /// <summary>
    ///     게스트 IP 주소 — VM 은 게스트 에이전트(network-get-interfaces), CT 는 interfaces(8.1+).
    ///     루프백·링크 로컬은 빼고 IPv4 를 앞에 둔다. 알 수 없으면(에이전트 없음·옛 PVE) 빈 목록.
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/qemu/{vmid}/agent/network-get-interfaces")]
    public async Task<IReadOnlyList<string>> GetGuestAddressesAsync(string node, ResourceKind kind, int vmid,
        CancellationToken ct = default)
    {
        IEnumerable<string> raw;
        try
        {
            raw = kind == ResourceKind.Qemu
                ? await AgentAddressesAsync(node, vmid, ct).ConfigureAwait(false)
                : (await Guests.CtInterfacesAsync(node, vmid, ct).ConfigureAwait(false))
                .SelectMany(row => new[]
                    { row.GetValueOrDefault("inet") ?? string.Empty, row.GetValueOrDefault("inet6") ?? string.Empty });
        }
        catch (Exception ex) when (ex is ProxmoxApiException or HttpRequestException or NotSupportedException
                                       or InvalidOperationException)
        {
            return [];
        }

        return raw.Select(a => a.Split('/')[0].Trim())
            .Where(a => IPAddress.TryParse(a, out var ip) && IsUsable(ip))
            .Distinct()
            .OrderBy(a => IPAddress.Parse(a).AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToList();
    }

    private async Task<IEnumerable<string>> AgentAddressesAsync(string node, int vmid, CancellationToken ct)
    {
        var json = Result(await GetJsonAsync($"nodes/{Escape(node)}/qemu/{vmid}/agent/network-get-interfaces", ct)
            .ConfigureAwait(false));
        if (json.ValueKind != JsonValueKind.Array) return [];

        var addresses = new List<string>();
        foreach (var nic in json.EnumerateArray())
            if (nic.TryGetProperty("ip-addresses", out var ips) && ips.ValueKind == JsonValueKind.Array)
                addresses.AddRange(ips.EnumerateArray().Select(ip => Text(ip, "ip-address")));
        return addresses;
    }

    private static bool IsUsable(IPAddress ip) =>
        !IPAddress.IsLoopback(ip) && !ip.IsIPv6LinkLocal
                                  && !(ip.AddressFamily == AddressFamily.InterNetwork
                                       && ip.GetAddressBytes() is [169, 254, ..]);
}

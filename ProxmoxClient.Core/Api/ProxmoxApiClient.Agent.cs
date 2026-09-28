using System.Text.Json;

namespace ProxmoxClient.Core.Api;

public sealed partial class ProxmoxApiClient
{
    /// <summary>
    ///     QEMU 게스트 에이전트가 알려 주는 게스트 안 정보 — 호스트 이름, OS, 네트워크 인터페이스별 MAC·IP.
    ///     (이름, 값) 행으로 돌려준다. 에이전트가 꺼져 있거나 응답이 없으면 서버 오류가 그대로 올라간다.
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/qemu/{vmid}/agent/get-host-name")]
    [Versioning.PveApi("GET", "/nodes/{node}/qemu/{vmid}/agent/get-osinfo")]
    [Versioning.PveApi("GET", "/nodes/{node}/qemu/{vmid}/agent/network-get-interfaces")]
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> GetAgentSummaryAsync(
        string node, int vmid, CancellationToken ct = default)
    {
        var basePath = $"nodes/{Escape(node)}/qemu/{vmid}/agent";
        var rows = new List<IReadOnlyDictionary<string, string>>();

        void Add(string name, string value)
        {
            if (value.Length > 0) rows.Add(new Dictionary<string, string> { ["name"] = name, ["value"] = value });
        }

        var host = Result(await GetJsonAsync($"{basePath}/get-host-name", ct).ConfigureAwait(false));
        Add("host-name", Text(host, "host-name"));

        var os = Result(await GetJsonAsync($"{basePath}/get-osinfo", ct).ConfigureAwait(false));
        Add("os", Text(os, "pretty-name").Length > 0 ? Text(os, "pretty-name") : Text(os, "name"));
        Add("kernel", Text(os, "kernel-release"));

        var interfaces = Result(await GetJsonAsync($"{basePath}/network-get-interfaces", ct).ConfigureAwait(false));
        if (interfaces.ValueKind == JsonValueKind.Array)
            foreach (var nic in interfaces.EnumerateArray())
            {
                var name = Text(nic, "name");
                if (name is "lo" or "Loopback Pseudo-Interface 1") continue; // 루프백은 뺀다

                var addresses = nic.TryGetProperty("ip-addresses", out var ips) && ips.ValueKind == JsonValueKind.Array
                    ? ips.EnumerateArray().Select(ip => $"{Text(ip, "ip-address")}/{Text(ip, "prefix")}")
                    : [];
                Add(name, string.Join("  ", new[] { Text(nic, "hardware-address") }.Concat(addresses)
                    .Where(v => v.Length > 0)));
            }

        return rows;
    }

    /// <summary>에이전트 응답은 {"result": …} 로 한 번 싸여 온다.</summary>
    private static JsonElement Result(in JsonElement data)
    {
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("result", out var result) ? result : data;
    }

    private static string Text(in JsonElement obj, string name)
    {
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value)
            ? ToText(value)
            : string.Empty;
    }

    /// <summary>
    ///     Ceph OSD 목록(GET nodes/{node}/ceph/osd) — 서버는 CRUSH 트리(root → host → osd)로 주므로
    ///     OSD 마다 한 행으로 펴고 속한 호스트 이름을 함께 담는다.
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/ceph/osd")]
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> GetCephOsdsAsync(
        string node, CancellationToken ct = default)
    {
        var data = await GetJsonAsync($"nodes/{Escape(node)}/ceph/osd", ct).ConfigureAwait(false);
        var rows = new List<IReadOnlyDictionary<string, string>>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("root", out var root)) Collect(root, "");
        return rows;

        void Collect(JsonElement item, string host)
        {
            var type = Text(item, "type");
            if (type == "osd")
            {
                var row = ToStringMap(item);
                row["host"] = host;
                rows.Add(row);
                return;
            }

            if (!item.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return;

            var nextHost = type == "host" ? Text(item, "name") : host;
            foreach (var child in children.EnumerateArray()) Collect(child, nextHost);
        }
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProxmoxClient.Core.Api;

/// <summary>클러스터 가입 정보 — 웹 UI 의 'Join Information' 과 같은 base64 문자열과 그 안의 주소·지문.</summary>
public sealed record ClusterJoinInfo(string Encoded, string IpAddress, string Fingerprint)
{
    /// <summary>기존 클러스터 노드의 corosync 링크(번호 → 주소) — 가입하는 노드도 링크마다 자기 주소가 필요하다.</summary>
    public IReadOnlyDictionary<int, string> PeerLinks { get; init; } = new Dictionary<int, string>();
}

public sealed partial class ProxmoxApiClient
{
    /// <summary>
    ///     다른 노드가 이 클러스터에 가입할 때 쓰는 정보(GET cluster/config/join).
    ///     웹 UI 와 같은 형식({ipAddress, fingerprint, peerLinks, ring_addr, totem} JSON 의 base64)으로 만들어
    ///     웹 UI 의 가입 창에 그대로 붙여 넣을 수 있다. 클러스터가 아니면 null.
    /// </summary>
    [Versioning.PveApi("GET", "/cluster/config/join")]
    public async Task<ClusterJoinInfo?> GetClusterJoinInfoAsync(CancellationToken ct = default)
    {
        var data = await GetJsonAsync("cluster/config/join", ct).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("nodelist", out var nodelist) || nodelist.ValueKind != JsonValueKind.Array)
            return null;

        var preferred = data.TryGetProperty("preferred_node", out var p) ? p.GetString() : null;
        var node = nodelist.EnumerateArray()
            .FirstOrDefault(n => n.TryGetProperty("name", out var name) && name.GetString() == preferred);
        if (node.ValueKind != JsonValueKind.Object) return null;

        // corosync 링크: ringN_addr(예전 이름) 또는 linkN
        var links = new JsonObject();
        var ringAddresses = new JsonArray();
        foreach (var property in node.EnumerateObject())
        {
            var match = System.Text.RegularExpressions.Regex.Match(property.Name, @"^(?:ring|link)(\d+)(?:_addr)?$");
            if (!match.Success) continue;

            var address = property.Value.GetString() ?? string.Empty;
            links[match.Groups[1].Value] = address;
            ringAddresses.Add(address);
        }

        var ip = node.TryGetProperty("pve_addr", out var addr) ? addr.GetString() ?? string.Empty : string.Empty;
        var fingerprint = node.TryGetProperty("pve_fp", out var fp) ? fp.GetString() ?? string.Empty : string.Empty;
        var info = new JsonObject
        {
            ["ipAddress"] = ip,
            ["fingerprint"] = fingerprint,
            ["peerLinks"] = links,
            ["ring_addr"] = ringAddresses,
            ["totem"] = data.TryGetProperty("totem", out var totem)
                ? JsonNode.Parse(totem.GetRawText())
                : new JsonObject()
        };

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(info.ToJsonString()));
        return new ClusterJoinInfo(encoded, ip, fingerprint);
    }

    /// <summary>
    ///     가입 정보 문자열을 풀어 가입할 클러스터의 주소와 지문을 얻는다. 형식이 맞지 않으면 null.
    /// </summary>
    public static ClusterJoinInfo? ParseClusterJoinInfo(string encoded)
    {
        try
        {
            var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim())));
            var ip = json?["ipAddress"]?.GetValue<string>();
            var fingerprint = json?["fingerprint"]?.GetValue<string>();
            if (ip is not { Length: > 0 } || fingerprint is not { Length: > 0 }) return null;

            var links = new SortedDictionary<int, string>();
            if (json?["peerLinks"] is JsonObject peerLinks)
                foreach (var (key, value) in peerLinks)
                    if (int.TryParse(key, out var number) && value is JsonValue address)
                        links[number] = address.ToString();
            return new ClusterJoinInfo(encoded.Trim(), ip, fingerprint) { PeerLinks = links };
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>응답을 보기 좋게 들여쓴 JSON 글로 돌려준다 — 구조가 깊어 표로 펴기 어려운 화면(Ceph 상태 등)용.</summary>
    public async Task<string> GetPrettyJsonAsync(string relativePath, CancellationToken ct = default)
    {
        var data = await GetJsonAsync(relativePath, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(data, PrettyJson);
    }

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

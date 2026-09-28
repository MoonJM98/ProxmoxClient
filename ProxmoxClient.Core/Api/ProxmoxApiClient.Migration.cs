using System.Text.Json;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Api;

/// <summary>게스트 이전 사전 확인 — 응답이 노드별 객체·배열로 깊어 표 대신 <see cref="MigrationCheck" /> 로 푼다.</summary>
public sealed partial class ProxmoxApiClient
{
    private static readonly Versioning.PveApiVersion CtMigrationCheck = new(9, 0);

    /// <summary>
    ///     이전 조건을 읽는다. CT 는 9.0 부터 있어 그보다 낮은 서버면 null(확인 없이 이전 창을 연다).
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/qemu/{vmid}/migrate")]
    [Versioning.PveApi("GET", "/nodes/{node}/lxc/{vmid}/migrate", Since = "9.0")]
    public async Task<MigrationCheck?> GetMigrationCheckAsync(string node, ResourceKind kind, int vmid,
        CancellationToken ct = default)
    {
        if (kind == ResourceKind.Lxc && !await SupportsAsync(CtMigrationCheck, ct).ConfigureAwait(false))
            return null;

        var data = await GetJsonAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/migrate", ct)
            .ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Object) return null;

        return new MigrationCheck(
            data.TryGetProperty("running", out var running) && ToText(running) is "1" or "true",
            Strings(data, "allowed_nodes"),
            NotAllowed(data),
            Array(data, "local_disks").Select(DiskText).ToList(),
            Strings(data, "local_resources"));
    }

    private static IReadOnlyList<string> Strings(in JsonElement data, string name)
    {
        return Array(data, name).Select(e => ToText(e)).Where(s => s.Length > 0).ToList();
    }

    private static IEnumerable<JsonElement> Array(JsonElement data, string name)
    {
        return data.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().ToList()
            : [];
    }

    /// <summary>
    ///     노드 → 막는 이유(쓸 수 없는 저장소·자원을 쉼표로). 서버는 모든 노드를 빈 객체로 넣어 두고 문제가 있을 때만
    ///     채우므로, 이유가 없는 노드는 옮길 수 있는 노드라 뺀다.
    /// </summary>
    private static Dictionary<string, string> NotAllowed(in JsonElement data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!data.TryGetProperty("not_allowed_nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var node in nodes.EnumerateObject())
        {
            var reasons = node.Value.ValueKind == JsonValueKind.Object
                ? node.Value.EnumerateObject().Select(r => ToText(r.Value)).Where(t => t.Length > 0)
                : [ToText(node.Value)];
            var why = string.Join(", ", reasons.Where(r => r is not ("" or "[]" or "{}")));
            if (why.Length > 0) result[node.Name] = why;
        }

        return result;
    }

    /// <summary>로컬 디스크 한 줄 — volid(크기, CD-ROM·안 쓰는 디스크 표시).</summary>
    private static string DiskText(JsonElement disk)
    {
        if (disk.ValueKind != JsonValueKind.Object) return ToText(disk);

        string V(string key) => disk.TryGetProperty(key, out var v) ? ToText(v) : string.Empty;
        var marks = new List<string>();
        if (V("cdrom") is "1" or "true") marks.Add("cdrom");
        if (V("is_unused") is "1" or "true") marks.Add("unused");
        if (V("size") is { Length: > 0 } size && long.TryParse(size, out var bytes) && bytes > 0)
            marks.Insert(0, $"{bytes / (1024.0 * 1024 * 1024):0.#} GiB");
        return marks.Count == 0 ? V("volid") : $"{V("volid")} ({string.Join(", ", marks)})";
    }
}

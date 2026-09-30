using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     리소스 매핑(PVE 8.0+) — PCI·USB, 디렉터리(8.3+). 이름 하나로 여러 노드의 같은 장치를 묶는다.
///     map 은 노드마다 한 줄("node=…,path=…")이고 줄마다 키를 반복해 보낸다(각 줄 안의 쉼표는 그대로).
/// </summary>
public sealed class MappingsApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private const string Dir = "8.3";
    private const string LiveMigration = "8.3";

    /// <summary>이 서버가 아는 매핑 종류(dir 은 8.3+).</summary>
    public IReadOnlyList<string> Kinds =>
        Api.Supports(PveApiVersion.Parse(Dir)) ? ["pci", "usb", "dir"] : ["pci", "usb"];

    /// <summary>그 매핑 종류(pci·usb·dir)를 이 서버에서 쓸 수 있는지 — 하위 탭·선택지용.</summary>
    public ApiFeature KindFeature(string kind)
    {
        return new ApiFeature(() => IsSupported(nameof(ListAsync)) && Kinds.Contains(kind));
    }

    /// <param name="checkNode">주면 그 노드에서 매핑이 맞는지 검사한 결과(checks)도 함께 받는다.</param>
    [PveApi("GET", "/cluster/mapping/pci", Since = "8.0")]
    [PveApi("GET", "/cluster/mapping/usb", Since = "8.0")]
    [PveApi("GET", "/cluster/mapping/dir", Since = Dir)]
    public async Task<IReadOnlyList<Row>> ListAsync(string kind, string? checkNode = null,
        CancellationToken ct = default)
    {
        await RequireKindAsync(kind, ct).ConfigureAwait(false);
        var query = checkNode is null ? string.Empty : $"?check-node={Uri.EscapeDataString(checkNode)}";
        return await Api.GetTableAsync($"cluster/mapping/{Seg(kind)}{query}", ct).ConfigureAwait(false);
    }

    [PveApi("GET", "/cluster/mapping/pci/{id}", Since = "8.0")]
    [PveApi("GET", "/cluster/mapping/usb/{id}", Since = "8.0")]
    [PveApi("GET", "/cluster/mapping/dir/{id}", Since = Dir)]
    public async Task<Row> GetAsync(string kind, string id, CancellationToken ct = default)
    {
        await RequireKindAsync(kind, ct).ConfigureAwait(false);
        return await Api.GetConfigLinesAsync($"cluster/mapping/{Seg(kind)}/{Seg(id)}", ct).ConfigureAwait(false);
    }

    /// <summary>values: id, map(줄마다), description, (PCI) mdev·live-migration-capable.</summary>
    [PveApi("POST", "/cluster/mapping/pci", Since = "8.0")]
    [PveApi("POST", "/cluster/mapping/usb", Since = "8.0")]
    [PveApi("POST", "/cluster/mapping/dir", Since = Dir)]
    [PveParam("live-migration-capable", LiveMigration)]
    public async Task<string> CreateAsync(string kind, IReadOnlyDictionary<string, string> values,
        CancellationToken ct = default)
    {
        await RequireKindAsync(kind, ct).ConfigureAwait(false);
        return await Api.SendPairsAsync(HttpMethod.Post, $"cluster/mapping/{Seg(kind)}",
            ToPairs(values, false, Api.Supports(PveApiVersion.Parse(LiveMigration))), ct).ConfigureAwait(false);
    }

    /// <summary>map 을 통째로 바꾼다. 비운 설명은 delete.</summary>
    [PveApi("PUT", "/cluster/mapping/pci/{id}", Since = "8.0")]
    [PveApi("PUT", "/cluster/mapping/usb/{id}", Since = "8.0")]
    [PveApi("PUT", "/cluster/mapping/dir/{id}", Since = Dir)]
    [PveParam("live-migration-capable", LiveMigration)]
    public async Task<string> UpdateAsync(string kind, string id, IReadOnlyDictionary<string, string> values,
        CancellationToken ct = default)
    {
        await RequireKindAsync(kind, ct).ConfigureAwait(false);
        return await Api.SendPairsAsync(HttpMethod.Put, $"cluster/mapping/{Seg(kind)}/{Seg(id)}",
            ToPairs(values, true, Api.Supports(PveApiVersion.Parse(LiveMigration))), ct).ConfigureAwait(false);
    }

    [PveApi("DELETE", "/cluster/mapping/pci/{id}", Since = "8.0")]
    [PveApi("DELETE", "/cluster/mapping/usb/{id}", Since = "8.0")]
    [PveApi("DELETE", "/cluster/mapping/dir/{id}", Since = Dir)]
    public async Task<string> DeleteAsync(string kind, string id, CancellationToken ct = default)
    {
        await RequireKindAsync(kind, ct).ConfigureAwait(false);
        return await Api.DeleteActionAsync($"cluster/mapping/{Seg(kind)}/{Seg(id)}", ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     화면 값 → 요청 쌍: map 은 줄마다 반복, 체크 칸은 추가 때 켠 것만(수정은 항상), 비운 설명은 수정 때 delete.
    ///     liveMigration 이 false(8.3 전 서버)면 live-migration-capable 을 보내지 않는다.
    /// </summary>
    public static List<KeyValuePair<string, string>> ToPairs(IReadOnlyDictionary<string, string> values,
        bool isEdit, bool liveMigration = true)
    {
        var pairs = values["map"].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => new KeyValuePair<string, string>("map", line)).ToList();
        if (values.TryGetValue("id", out var id)) pairs.Add(new("id", id));
        var flags = liveMigration ? new[] { "mdev", "live-migration-capable" } : ["mdev"];
        foreach (var key in flags)
            if (values.TryGetValue(key, out var flag) && (isEdit || flag == "1")) pairs.Add(new(key, flag));
        var description = values.TryGetValue("description", out var d) ? d : string.Empty;
        if (description.Length > 0) pairs.Add(new("description", description));
        else if (isEdit) pairs.Add(new("delete", "description"));
        return pairs;
    }

    private async Task RequireKindAsync(string kind, CancellationToken ct)
    {
        await Api.RequireAsync(GetType(), ct, nameof(ListAsync)).ConfigureAwait(false);
        if (kind == "dir" && !await SupportsAsync(Dir, ct).ConfigureAwait(false))
            throw new ProxmoxApiException(Localization.Res.T("Api_VersionRequired", Dir, Api.ServerVersion));
    }
}

using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     노드 디스크 — 목록·SMART·GPT·지우기, LVM·LVM-thin·디렉터리·ZFS 저장소 만들기·없애기.
///     버전 차이: 저장소 없애기(7.1+), ZFS dRAID(7.2+).
/// </summary>
public sealed class DisksApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private static string D(string node) => $"nodes/{Seg(node)}/disks";

    /// <param name="unusedOnly">아무도 쓰지 않는 디스크만(새 저장소·OSD 용).</param>
    [PveApi("GET", "/nodes/{node}/disks/list")]
    public Task<IReadOnlyList<Row>> ListAsync(string node, bool unusedOnly = false, CancellationToken ct = default)
    {
        return Api.GetTableAsync(unusedOnly ? $"{D(node)}/list?type=unused" : $"{D(node)}/list", ct);
    }

    /// <summary>SMART 속성표(ATA). NVMe 등은 빈 목록 — <see cref="SmartTextAsync" /> 를 쓴다.</summary>
    [PveApi("GET", "/nodes/{node}/disks/smart")]
    public Task<IReadOnlyList<Row>> SmartAttributesAsync(string node, string disk, CancellationToken ct = default)
    {
        return Api.GetArrayPropertyAsync($"{D(node)}/smart?disk={Uri.EscapeDataString(disk)}", "attributes", ct);
    }

    [PveApi("GET", "/nodes/{node}/disks/smart")]
    public async Task<string> SmartTextAsync(string node, string disk, CancellationToken ct = default)
    {
        var smart = await Api.GetObjectAsync($"{D(node)}/smart?disk={Uri.EscapeDataString(disk)}", ct)
            .ConfigureAwait(false);
        return smart.TryGetValue("text", out var text) ? text : string.Empty;
    }

    /// <summary>빈 디스크에 GPT 를 만든다(작업 UPID).</summary>
    [PveApi("POST", "/nodes/{node}/disks/initgpt")]
    public Task<string> InitGptAsync(string node, string disk, CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{D(node)}/initgpt", new Dictionary<string, string> { ["disk"] = disk }, ct);
    }

    /// <summary>디스크·파티션을 지운다(작업 UPID).</summary>
    [PveApi("PUT", "/nodes/{node}/disks/wipedisk")]
    public Task<string> WipeAsync(string node, string disk, CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{D(node)}/wipedisk", new Dictionary<string, string> { ["disk"] = disk }, ct);
    }

    /// <summary>LVM 볼륨 그룹(목록 응답의 children).</summary>
    [PveApi("GET", "/nodes/{node}/disks/lvm")]
    public Task<IReadOnlyList<Row>> VolumeGroupsAsync(string node, CancellationToken ct = default)
    {
        return Api.GetArrayPropertyAsync($"{D(node)}/lvm", "children", ct);
    }

    /// <param name="kind">lvmthin·directory·zfs.</param>
    [PveApi("GET", "/nodes/{node}/disks/lvmthin")]
    [PveApi("GET", "/nodes/{node}/disks/directory")]
    [PveApi("GET", "/nodes/{node}/disks/zfs")]
    public Task<IReadOnlyList<Row>> ListStorageAsync(string node, string kind, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{D(node)}/{Kind(kind)}", ct);
    }

    /// <summary>저장소 만들기(작업 UPID) — kind: lvm·lvmthin·directory·zfs.</summary>
    [PveApi("POST", "/nodes/{node}/disks/lvm")]
    [PveApi("POST", "/nodes/{node}/disks/lvmthin")]
    [PveApi("POST", "/nodes/{node}/disks/directory")]
    [PveApi("POST", "/nodes/{node}/disks/zfs")]
    [PveParam("draid-config", "7.2")]
    public Task<string> CreateStorageAsync(string node, string kind, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{D(node)}/{Kind(kind)}", Supported(form), ct);
    }

    /// <summary>
    ///     저장소 없애기(7.1+, 작업 UPID). cleanupDisks 면 디스크도 지운다. LVM-thin 은 볼륨 그룹이 필요하다.
    /// </summary>
    [PveApi("DELETE", "/nodes/{node}/disks/lvm/{name}", Since = "7.1")]
    [PveApi("DELETE", "/nodes/{node}/disks/lvmthin/{name}", Since = "7.1")]
    [PveApi("DELETE", "/nodes/{node}/disks/directory/{name}", Since = "7.1")]
    [PveApi("DELETE", "/nodes/{node}/disks/zfs/{name}", Since = "7.1")]
    public async Task<string> DeleteStorageAsync(string node, string kind, string name, bool cleanupConfig,
        bool cleanupDisks, string? volumeGroup = null, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        var query = $"cleanup-config={(cleanupConfig ? 1 : 0)}&cleanup-disks={(cleanupDisks ? 1 : 0)}";
        if (volumeGroup is not null) query += $"&volume-group={Uri.EscapeDataString(volumeGroup)}";
        return await Api.DeleteActionAsync($"{D(node)}/{Kind(kind)}/{Seg(name)}?{query}", ct).ConfigureAwait(false);
    }

    private static string Kind(string kind)
    {
        return kind is "lvm" or "lvmthin" or "directory" or "zfs"
            ? kind
            : throw new ArgumentException($"Unknown disk storage kind '{kind}'", nameof(kind));
    }
}

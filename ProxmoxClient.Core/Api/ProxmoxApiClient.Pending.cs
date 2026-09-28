using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Api;

/// <summary>게스트 설정의 대기 중 변경 — 조회(…/pending)와 되돌리기(PUT …/config revert=).</summary>
public sealed partial class ProxmoxApiClient
{
    /// <summary>현재 값·대기 중 변경을 함께 읽는다(GET nodes/{node}/{kind}/{vmid}/pending).</summary>
    [Versioning.PveApi("GET", "/nodes/{node}/qemu/{vmid}/pending")]
    [Versioning.PveApi("GET", "/nodes/{node}/lxc/{vmid}/pending")]
    public async Task<GuestPendingConfig> GetGuestPendingAsync(string node, ResourceKind kind, int vmid,
        CancellationToken ct = default)
    {
        var rows = await GetTableAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/pending", ct)
            .ConfigureAwait(false);
        return GuestPendingConfig.FromRows(rows);
    }

    /// <summary>대기 중 변경을 취소한다(PUT …/config revert=키1,키2).</summary>
    [Versioning.PveApi("PUT", "/nodes/{node}/qemu/{vmid}/config")]
    [Versioning.PveApi("PUT", "/nodes/{node}/lxc/{vmid}/config")]
    public Task<string> RevertGuestPendingAsync(string node, ResourceKind kind, int vmid,
        IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        return UpdateGuestConfigAsync(node, kind, vmid,
            new Dictionary<string, string> { ["revert"] = string.Join(',', keys) }, ct);
    }
}

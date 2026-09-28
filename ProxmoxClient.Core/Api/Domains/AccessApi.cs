using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>그룹·역할·권한(ACL) — 7.0~9 사이에 파라미터 차이가 없다.</summary>
public sealed class AccessApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    [PveApi("GET", "/access/groups")]
    public Task<IReadOnlyList<Row>> ListGroupsAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("access/groups", ct);
    }

    [PveApi("POST", "/access/groups")]
    public Task<string> CreateGroupAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PostActionAsync("access/groups", form, ct);
    }

    /// <summary>이 API 에는 delete 가 없다 — 비운 설명은 빈 문자열로 보낸다.</summary>
    [PveApi("PUT", "/access/groups/{groupid}")]
    public Task<string> UpdateGroupAsync(string groupId, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"access/groups/{Seg(groupId)}", form, ct);
    }

    [PveApi("DELETE", "/access/groups/{groupid}")]
    public Task<string> DeleteGroupAsync(string groupId, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"access/groups/{Seg(groupId)}", ct);
    }

    [PveApi("GET", "/access/roles")]
    public Task<IReadOnlyList<Row>> ListRolesAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("access/roles", ct);
    }

    /// <summary>역할의 권한 — { 권한 이름: 1 }. Administrator 를 읽으면 서버가 아는 모든 권한이 나온다.</summary>
    [PveApi("GET", "/access/roles/{roleid}")]
    public Task<Row> GetRoleAsync(string roleId, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"access/roles/{Seg(roleId)}", ct);
    }

    [PveApi("POST", "/access/roles")]
    public Task<string> CreateRoleAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PostActionAsync("access/roles", form, ct);
    }

    [PveApi("PUT", "/access/roles/{roleid}")]
    public Task<string> UpdateRoleAsync(string roleId, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"access/roles/{Seg(roleId)}", form, ct);
    }

    [PveApi("DELETE", "/access/roles/{roleid}")]
    public Task<string> DeleteRoleAsync(string roleId, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"access/roles/{Seg(roleId)}", ct);
    }

    [PveApi("GET", "/access/acl")]
    public Task<IReadOnlyList<Row>> ListAclAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("access/acl", ct);
    }

    /// <summary>권한 주기·빼기 — path, roles, users/groups/tokens, propagate, (빼기) delete=1.</summary>
    [PveApi("PUT", "/access/acl")]
    public Task<string> UpdateAclAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PutActionAsync("access/acl", form, ct);
    }
}

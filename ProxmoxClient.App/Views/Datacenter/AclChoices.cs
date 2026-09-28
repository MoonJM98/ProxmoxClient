using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     권한 추가 창의 목록 — 대상(사용자·그룹·API 토큰)과 경로(웹 UI pveACLPathSelector 와 같은 모양).
///     대상 값은 "종류:ID" 로 담아 한 칸에서 고르게 한다.
/// </summary>
internal static class AclChoices
{
    private static readonly string[] StaticPaths =
    [
        "/", "/access", "/access/groups", "/access/realm", "/mapping", "/mapping/dir", "/mapping/pci",
        "/mapping/usb", "/nodes", "/pool", "/sdn", "/sdn/zones", "/storage", "/vms"
    ];

    /// <summary>"user:root@pam" → ("user", "root@pam").</summary>
    public static (string Type, string Id) Split(string value)
    {
        var colon = value.IndexOf(':');
        return colon < 0 ? ("user", value) : (value[..colon], value[(colon + 1)..]);
    }

    public static async Task<IReadOnlyList<(string, string)>> PrincipalsAsync(ProxmoxApiClient api)
    {
        var users = (await api.Users.ListAsync()).Select(r => r["userid"])
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
        var groups = (await api.Access.ListGroupsAsync()).Select(r => r["groupid"])
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase);

        var result = new List<(string, string)>();
        result.AddRange(users.Select(u => ($"user:{u}", Loc.T("DcAcl_WhoUser", u))));
        result.AddRange(groups.Select(g => ($"group:{g}", Loc.T("DcAcl_WhoGroup", g))));
        foreach (var user in users)
        {
            // 토큰은 사용자별로만 조회된다 — 권한이 없어 못 읽는 사용자는 건너뛴다
            IReadOnlyList<IReadOnlyDictionary<string, string>> tokens;
            try
            {
                tokens = await api.Users.ListTokensAsync(user);
            }
            catch (ProxmoxApiException)
            {
                continue;
            }

            result.AddRange(tokens.Where(t => t.ContainsKey("tokenid"))
                .Select(t => $"{user}!{t["tokenid"]}")
                .Select(id => ($"token:{id}", Loc.T("DcAcl_WhoToken", id))));
        }

        return result;
    }

    /// <summary>고정 경로 + 노드·게스트·저장소·풀·SDN 영역(cluster/resources).</summary>
    public static async Task<IReadOnlyList<(string, string)>> PathsAsync(ProxmoxApiClient api)
    {
        var paths = new SortedSet<string>(StaticPaths, StringComparer.Ordinal);
        IReadOnlyList<IReadOnlyDictionary<string, string>> resources;
        try
        {
            resources = await api.Cluster.ResourcesAsync();
        }
        catch (ProxmoxApiException)
        {
            resources = [];
        }

        foreach (var r in resources)
        {
            string V(string key) => r.TryGetValue(key, out var v) ? v : string.Empty;
            var path = V("type") switch
            {
                "node" => $"/nodes/{V("node")}",
                "qemu" or "lxc" => $"/vms/{V("vmid")}",
                "storage" => $"/storage/{V("storage")}",
                "pool" => $"/pool/{V("pool")}",
                "sdn" when V("sdn").Length > 0 => $"/sdn/zones/{V("sdn")}",
                _ => null
            };
            if (path is not null && !path.EndsWith('/')) paths.Add(path);
        }

        return paths.Select(p => (p, p)).ToList();
    }
}

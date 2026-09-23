namespace ProxmoxClient.Core.Models;

/// <summary>
///     방화벽이 걸리는 범위 — 데이터센터·노드·게스트마다 같은 규칙/옵션 API 가 경로만 달리해 존재한다.
/// </summary>
public sealed class FirewallScope
{
    private FirewallScope(string basePath, string? rulesPath = null, bool hasOptions = true)
    {
        BasePath = basePath;
        RulesPath = rulesPath ?? $"{basePath}/rules";
        HasOptions = hasOptions;
    }

    /// <summary>".../firewall" 까지의 API 상대 경로(이미 이스케이프됨).</summary>
    public string BasePath { get; }

    /// <summary>규칙 목록 경로 — 보통 {BasePath}/rules, 보안 그룹은 그룹 경로 자체.</summary>
    public string RulesPath { get; }

    /// <summary>사용 여부 등 옵션이 있는지 — 보안 그룹에는 없다.</summary>
    public bool HasOptions { get; }

    public static FirewallScope Cluster { get; } = new("cluster/firewall");

    /// <summary>데이터센터 보안 그룹 — 여러 게스트가 함께 쓰는 규칙 묶음.</summary>
    public static FirewallScope ForSecurityGroup(string group)
    {
        return new FirewallScope("cluster/firewall",
            $"cluster/firewall/groups/{Uri.EscapeDataString(group)}", hasOptions: false);
    }

    public static FirewallScope ForNode(string node)
    {
        return new FirewallScope($"nodes/{Uri.EscapeDataString(node)}/firewall");
    }

    public static FirewallScope ForGuest(string node, ResourceKind kind, int vmid)
    {
        return new FirewallScope($"nodes/{Uri.EscapeDataString(node)}/{kind.ApiSegment()}/{vmid}/firewall");
    }
}

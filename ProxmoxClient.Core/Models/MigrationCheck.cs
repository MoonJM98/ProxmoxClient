namespace ProxmoxClient.Core.Models;

/// <summary>
///     이전 전에 서버가 알려 주는 조건(GET .../migrate) — 옮길 수 있는 노드와 막는 이유, 함께 옮겨야 할 로컬 디스크,
///     온라인 이전을 막는 로컬 자원(USB·PCI 통과 등).
/// </summary>
public sealed record MigrationCheck(
    bool Running,
    IReadOnlyList<string> AllowedNodes,
    IReadOnlyDictionary<string, string> NotAllowedNodes,
    IReadOnlyList<string> LocalDisks,
    IReadOnlyList<string> LocalResources)
{
    /// <summary>로컬 디스크가 있으면 '로컬 디스크와 함께' 이전이 필요하다.</summary>
    public bool NeedsLocalDisks => LocalDisks.Count > 0;
}

namespace ProxmoxClient.Core.Models;

/// <summary>
///     GET /cluster/resources 한 번으로 받은 클러스터 전체 현황.
///     게스트·노드·스토리지의 상태·업타임·CPU·메모리·디스크가 모두 들어 있어, 항목마다 따로 조회할 필요가 없다.
///     (노드의 커널·평균 부하·스왑 등 세부 값은 여기 없고 nodes/{node}/status 에서 가져온다)
/// </summary>
public sealed record ClusterOverview(
    IReadOnlyList<PveResource> Guests,
    IReadOnlyList<PveNode> Nodes,
    IReadOnlyList<PveStorage> Storages);
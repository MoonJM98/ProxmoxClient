using ProxmoxClient.Presentation.Shared;

namespace ProxmoxClient.Presentation.Datacenter;

/// <summary>
///     데이터센터 목록형 화면 — 여러 작업이 동시에 옮기도록 영역별 partial 파일로 나눴다:
///     DatacenterTables.Access.cs(사용자·그룹·역할·TFA·권한·영역·풀), DatacenterTables.Cluster.cs(요약·저장소·백업·복제),
///     DatacenterTables.Ha.cs(HA). 여기에는 함께 쓰는 도우미만 둔다.
/// </summary>
internal static partial class DatacenterTables
{
    private static TableSpec Create(Func<Task<IReadOnlyList<IReadOnlyDictionary<string, string>>>> load,
        IReadOnlyList<TableColumn> columns, string hintKey, IReadOnlyList<TableAction>? actions = null)
    {
        return new TableSpec(load, columns, hintKey, actions);
    }
}

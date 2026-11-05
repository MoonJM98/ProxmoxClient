using Avalonia.Controls;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.UI.Services;

/// <summary>
///     옮겨 온 기능의 진입점 등록부. 각 기능은 자기 폴더의 [ModuleInitializer] 에서 여기에 등록한다 —
///     공용 화면 파일을 고치지 않으므로 여러 기능을 동시에 옮겨도 충돌하지 않는다.
///     등록되지 않은 진입점은 화면에 나타나지 않는다.
/// </summary>
public static class FeatureRegistry
{
    /// <summary>데이터센터(클러스터 전체) 탐색 화면. 클러스터 화면의 "데이터센터" 항목이 연다.</summary>
    public static Func<ServerSession, Control>? Datacenter { get; set; }

    /// <summary>노드 관리 탐색 화면. 노드 상세의 "노드 관리" 항목이 연다.</summary>
    public static Func<ServerSession, PveNode, Control>? Node { get; set; }

    /// <summary>스토리지 탐색 화면. 스토리지 목록의 행을 누르면 연다.</summary>
    public static Func<ServerSession, PveStorage, Control>? Storage { get; set; }

    /// <summary>게스트 상세의 "관리" 목록에 들어가는 항목(하드웨어·옵션·백업·방화벽 등).</summary>
    public static List<GuestFeature> GuestFeatures { get; } = [];

    /// <summary>클러스터 화면의 "만들기" 메뉴 항목(VM·CT 생성 마법사 등).</summary>
    public static List<ClusterFeature> ClusterFeatures { get; } = [];
}

/// <param name="Order">목록 순서(작을수록 위).</param>
/// <param name="IsAvailable">이 게스트에서 쓸 수 있는지(없으면 항상).</param>
public sealed record GuestFeature(
    string Id,
    string LabelKey,
    string IconKey,
    int Order,
    Func<ServerSession, PveResource, Control> Create,
    Func<PveResource, bool>? IsAvailable = null);

/// <summary>클러스터 화면의 실행 항목 — 화면을 열지 않고 흐름(마법사 등)을 직접 실행한다.</summary>
public sealed record ClusterFeature(string Id, string LabelKey, string IconKey, int Order,
    Func<ServerSession, Task> Run);

/// <summary>
///     검증용 미리보기 — 아직 탐색 화면에 연결하지 않은(옮기는 중인) 탭을 화면 검증 도구(tools/UiSnapshot,
///     target "preview:영역")가 열어 볼 수 있게 등록한다. 앱 화면에는 나타나지 않는다.
/// </summary>
public static class PreviewRegistry
{
    private static readonly Dictionary<string, List<PreviewTab>> Areas = new(StringComparer.Ordinal);

    public static void Add(string area, PreviewTab tab)
    {
        lock (Areas)
        {
            if (!Areas.TryGetValue(area, out var list)) Areas[area] = list = [];
            list.Add(tab);
        }
    }

    public static IReadOnlyList<PreviewTab> Get(string area)
    {
        lock (Areas) return Areas.TryGetValue(area, out var list) ? list.ToList() : [];
    }
}

public sealed record PreviewTab(string Id, string LabelKey, string IconKey, Func<PreviewContext, Control> Create);

/// <summary>미리보기 탭이 받는 것 — 로그인한 세션과 가짜 서버의 대표 대상(VM 100 · CT 200 · 노드 pve1 · 스토리지 local).</summary>
public sealed record PreviewContext(
    ServerSession Session,
    PveResource Vm,
    PveResource Ct,
    PveNode Node,
    PveStorage Storage)
{
    public Core.Api.ProxmoxApiClient Api => Session.Api;

    public PermissionsInfo Permissions => Session.Permissions;
}

using System.Runtime.CompilerServices;
using Avalonia.Controls;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Create;

/// <summary>
///     VM·CT 만들기 마법사의 진입점(클러스터 "관리" 목록)과 검증용 미리보기(tools/UiSnapshot, target "preview:create").
///     WPF 의 "만들기" 메뉴와 같은 순서(VM 10 · CT 20)·같은 문구 키를 쓴다.
/// </summary>
internal static class CreateModule
{
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255",
        Justification = "포팅 가이드가 정한 등록 방식 — 검증 도구가 모듈 로드 때 미리보기를 찾는다.")]
    internal static void Register()
    {
        FeatureRegistry.ClusterFeatures.Add(new ClusterFeature("create-vm", "Wz_CreateVm", "IconPlus", 10,
            session => OpenAsync(session, "Wz_CreateVm", VmWizard.Create)));
        FeatureRegistry.ClusterFeatures.Add(new ClusterFeature("create-ct", "Wz_CreateCt", "IconPlus", 20,
            session => OpenAsync(session, "Wz_CreateCt", CtWizard.Create)));

        PreviewRegistry.Add("create", new PreviewTab("vm", "Wz_CreateVm", "IconPlus",
            ctx => VmWizard.Create(ctx.Api, [ctx.Node.Node])));
        PreviewRegistry.Add("create", new PreviewTab("ct", "Wz_CreateCt", "IconPlus",
            ctx => CtWizard.Create(ctx.Api, [ctx.Node.Node])));
    }

    /// <summary>
    ///     마법사 화면을 곧바로 열고, 온라인 노드 목록은 화면이 진행 표시와 함께 읽는다(WPF 와 같이 온라인 노드만 고를 수 있다).
    ///     실제 만들기는 화면 안의 "만들기"가 보내며, 그 POST 는 평소 확인 절차(WriteConfirmation)를 거친다.
    /// </summary>
    private static Task OpenAsync(ServerSession session, string titleKey,
        Func<ProxmoxApiClient, Func<Task<IReadOnlyList<string>>>, Control> create)
    {
        async Task<IReadOnlyList<string>> OnlineNodes()
        {
            var nodes = await session.Api.GetNodesAsync();
            return nodes.Where(n => n.IsOnline).Select(n => n.Node)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        return AppServices.Navigator.PushViewAsync(L.T(titleKey), create(session.Api, OnlineNodes));
    }
}

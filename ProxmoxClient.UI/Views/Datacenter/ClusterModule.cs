using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Datacenter;

/// <summary>
///     데이터센터 클러스터 영역(검색·요약·옵션·저장소·백업·복제·CPU 모델·클러스터 로그)의 검증 등록.
///     아직 데이터센터 탐색 화면에 연결하지 않았으므로 미리보기(tools/UiSnapshot, target "preview:dc-cluster")로만 열린다.
///     탐색 화면을 연결할 때는 DatacenterNavigator 를 옮기면서 같은 항목을 NavTabList 에 넣는다.
/// </summary>
internal static class ClusterModule
{
    // 가이드가 정한 검증 등록 방식(미리보기 등록부)이라 라이브러리 경고(CA2255)는 의도적으로 끈다
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "검증 등록부 등록 — porting-guide 의 등록 방식")]
    internal static void Register()
    {
        const string area = "dc-cluster";
        PreviewRegistry.Add(area, new PreviewTab("search", "DcTab_Search", "IconSearch", ctx =>
            SearchTab.Create(ctx.Api, null,
                ClusterBulkActions.Create(ctx.Api, ctx.Permissions.CanPowerMgmt, ctx.Permissions.CanMigrate))));
        PreviewRegistry.Add(area, new PreviewTab("summary", "GuestTab_Summary", "IconList",
            ctx => DatacenterTables.Summary(ctx.Api)));
        PreviewRegistry.Add(area, new PreviewTab("options", "GuestTab_Options", "IconSettings",
            ctx => DatacenterOptions.Create(ctx.Api)));
        PreviewRegistry.Add(area, new PreviewTab("storage", "DcTab_Storage", "IconDatabase",
            ctx => DatacenterTables.Storage(ctx.Api, true)));
        PreviewRegistry.Add(area, new PreviewTab("backup", "GuestTab_Backup", "IconArchive",
            ctx => DatacenterTables.BackupJobs(ctx.Api, true)));
        PreviewRegistry.Add(area, new PreviewTab("replication", "DcTab_Replication", "IconCopy",
            ctx => DatacenterTables.Replication(ctx.Api, true)));
        PreviewRegistry.Add(area, new PreviewTab("cpumodels", "CpuModel_Tab", "IconSettings", ctx =>
            ctx.Api.Cluster.Feature(nameof(ClusterApi.CpuModelsAsync)).IsAvailable
                ? CpuModelsTab.Create(ctx.Api, true)
                // 탐색 창은 서버가 못 쓰는 탭을 빼지만, 미리보기는 탭을 그대로 열므로 안내만 보여 준다
                : new TextBlock
                {
                    Text = L.T("DcCluster_CpuModelsUnsupported"), Classes = { "subtle" },
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12)
                }));
        PreviewRegistry.Add(area, new PreviewTab("clusterlog", "ClusterLog_Tab", "IconList",
            ctx => ClusterLogTab.Create(ctx.Api)));
    }
}

using System.Runtime.CompilerServices;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Guest.Tabs;

/// <summary>
///     게스트 탭 검증 등록 — 화면 검증 도구(tools/UiSnapshot, target "preview:guest-tabs")가 여는 탭들.
///     앱 화면에는 나타나지 않는다. 실제 진입점은 게스트 상세의 "관리" 목록(FeatureRegistry.GuestFeatures)이다.
/// </summary>
internal static class GuestTabsModule
{
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255",
        Justification = "검증 등록은 모듈 초기화에서 하도록 정해진 방식(porting-guide 5항)")]
    internal static void Register()
    {
        const string area = "guest-tabs";
        PreviewRegistry.Add(area, new PreviewTab("summary", "GuestTab_Summary", "IconList",
            context => new SummaryTab(context.Api, context.Vm, [])));
        PreviewRegistry.Add(area, new PreviewTab("monitor", "GuestTab_Monitor", "IconTerminal",
            context => new MonitorTab(context.Api, context.Vm)));
        PreviewRegistry.Add(area, new PreviewTab("options", "GuestTab_Options", "IconSettings",
            context => OptionsFor(context, context.Vm)));
        PreviewRegistry.Add(area, new PreviewTab("snapshots", "GuestTab_Snapshots", "IconCamera",
            context => new SnapshotsTab(context.Api, context.Vm)));
        PreviewRegistry.Add(area, new PreviewTab("backup", "GuestTab_Backup", "IconDatabase",
            context => new GuestChipTabs(
                ("BackupList_Now", () => new BackupTab(context.Api, context.Vm)),
                ("BackupList_Tab", () => BackupList.Create(context.Api, context.Vm, context.Permissions.CanAllocate)))));
        // 백업 목록은 위 "백업" 탭의 두 번째 칸 — 검증 캡처를 위해 따로도 연다
        PreviewRegistry.Add(area, new PreviewTab("backup-list", "BackupList_Tab", "IconDatabase",
            context => BackupList.Create(context.Api, context.Vm, context.Permissions.CanAllocate)));
        PreviewRegistry.Add(area, new PreviewTab("tasks", "GuestTab_Tasks", "IconArchive",
            context => new TasksTab(
                () => context.Api.GetNodeTasksAsync(context.Vm.Node, vmid: context.Vm.VmId),
                "TasksTab_Hint", "TasksTab_Empty", context.Api)));
        PreviewRegistry.Add(area, new PreviewTab("ct-options", "GuestTab_Options", "IconSettings",
            context => OptionsFor(context, context.Ct)));
    }

    /// <summary>게스트 설정 옵션 탭 — 게스트 종류에 맞는 항목만, 저장·되돌리기는 게스트 설정 API 로.</summary>
    private static OptionsTab OptionsFor(PreviewContext context, PveResource guest)
    {
        var api = context.Api;
        var node = guest.Node;
        var kind = guest.Kind;
        var vmid = guest.VmId;
        return new OptionsTab(
            GuestOptions.For(kind),
            () => api.GetGuestPendingAsync(node, kind, vmid),
            changes => api.UpdateGuestConfigAsync(node, kind, vmid, FormHelpers.UpdateForm(changes)),
            keys => api.RevertGuestPendingAsync(node, kind, vmid, keys),
            null,
            api.Guests.Feature(nameof(GuestsApi.SetConfigAsync)));
    }
}

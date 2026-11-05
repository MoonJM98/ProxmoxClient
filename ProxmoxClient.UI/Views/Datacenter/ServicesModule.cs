using System.Runtime.CompilerServices;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Guest.Tabs;

namespace ProxmoxClient.UI.Views.Datacenter;

/// <summary>
///     데이터센터 "서비스" 영역(클러스터·HA·SDN·ACME·방화벽·지표·매핑·알림, 게스트 방화벽)의 검증 등록.
///     아직 탐색 창에 연결하지 않았으므로 PreviewRegistry 로만 연다(tools/UiSnapshot --target preview:dc-services).
///     권한은 검증에서 모두 허용(편집 가능)으로 본다.
/// </summary>
internal static class ServicesModule
{
    // 검증 등록은 모듈 초기화기로 한다(porting-guide 5항). CA2255 는 라이브러리에서의 사용을 경고하지만, 이 등록은 의도한 것이다.
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries")]
    internal static void Register()
    {
        PreviewRegistry.Add("dc-services", new PreviewTab("cluster", "DcTab_Cluster", "IconServer",
            ctx => ClusterTabs.Cluster(ctx.Api, true)));
        PreviewRegistry.Add("dc-services", new PreviewTab("ha", "DcTab_Ha", "IconRotate",
            ctx => DatacenterTables.Ha(ctx.Api, true)));
        PreviewRegistry.Add("dc-services", new PreviewTab("sdn", "DcTab_Sdn", "IconSwitch",
            ctx => ClusterTabs.Sdn(ctx.Api, true)));
        PreviewRegistry.Add("dc-services", new PreviewTab("acme", "DcTab_Acme", "IconShield",
            ctx => AcmeTabs.Create(ctx.Api, true)));
        PreviewRegistry.Add("dc-services", new PreviewTab("firewall", "GuestTab_Firewall", "IconShield",
            ctx => FirewallTabs.Create(ctx.Api, true)));
        PreviewRegistry.Add("dc-services", new PreviewTab("metrics", "DcTab_Metrics", "IconList",
            ctx => MonitoringTabs.Metrics(ctx.Api, true)));
        PreviewRegistry.Add("dc-services", new PreviewTab("mappings", "DcTab_Mappings", "IconLink",
            ctx => ClusterTabs.Mappings(ctx.Api, true)));
        PreviewRegistry.Add("dc-services", new PreviewTab("notifications", "DcTab_Notifications", "IconExternal",
            ctx => MonitoringTabs.Notifications(ctx.Api, true)));
        PreviewRegistry.Add("dc-services", new PreviewTab("guestfw", "GuestTab_Firewall", "IconShield",
            ctx => new FirewallTab(ctx.Api, FirewallScope.ForGuest(ctx.Vm.Node, ctx.Vm.Kind, ctx.Vm.VmId), true)));
    }
}

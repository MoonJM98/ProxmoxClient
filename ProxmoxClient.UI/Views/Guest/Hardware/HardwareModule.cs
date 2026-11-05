using System.Runtime.CompilerServices;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Guest.Tabs;

namespace ProxmoxClient.UI.Views.Guest.Hardware;

/// <summary>
///     하드웨어 화면의 검증용 등록(tools/UiSnapshot, target "preview:guest-hardware").
///     탐색 화면(게스트 관리 목록)에는 아직 연결하지 않았다 — 그 연결은 게스트 탭 작업에서 FeatureRegistry 로 한다.
///     VM 100(pve1)과 CT 200(pve2) 을 대상으로, 탭의 두 하위 화면(목록·간편)을 각각 연다.
/// </summary>
internal static class HardwareModule
{
    [ModuleInitializer]
    internal static void Register()
    {
        PreviewRegistry.Add("guest-hardware", new PreviewTab("vm", "GuestHardware_All", "IconBox",
            ctx => new HardwareView(ctx.Api, ctx.Vm)));
        PreviewRegistry.Add("guest-hardware", new PreviewTab("ct", "GuestHardware_All", "IconBox",
            ctx => new HardwareView(ctx.Api, ctx.Ct)));
        PreviewRegistry.Add("guest-hardware", new PreviewTab("vm-quick", "GuestHardware_Quick", "IconBox",
            ctx => new HardwareTab(ctx.Api, ctx.Vm)));
        PreviewRegistry.Add("guest-hardware", new PreviewTab("ct-quick", "GuestHardware_Quick", "IconBox",
            ctx => new HardwareTab(ctx.Api, ctx.Ct)));
    }
}

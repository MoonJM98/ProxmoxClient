using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Guest;

/// <summary>
///     게스트 영역의 등록. 실제 진입점은 <see cref="FeatureRegistry.GuestFeatures" />다 — 게스트 상세의 "관리" 목록(모바일)과
///     메인 화면 버튼(설정·복제·백업·방화벽·스냅숏)이 여기서 항목을 찾는다. 탭마다 검증용 미리보기도 등록한다
///     (tools/UiSnapshot, target "preview:guest").
/// </summary>
internal static class GuestModule
{
    // 모듈 초기화기로 등록한다(porting-guide 5항). CA2255 는 라이브러리 사용 경고지만, 이 등록은 의도한 것이다.
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "진입점·검증 등록은 모듈 초기화에서 하도록 정해진 방식(porting-guide 5항)")]
    internal static void Register()
    {
        RegisterFeatures();
        RegisterPreviews();
    }

    private static void RegisterFeatures()
    {
        // "관리" 목록 순서 — 요약은 상세 화면 자체라 뺀다. 하드웨어·리소스처럼 VM·CT 이름이 다른 탭은 종류별로 따로 등록한다.
        var order = 0;
        foreach (var tab in GuestTabs.All)
        {
            order += 10;
            if (tab.Id == "summary") continue;

            if (tab.CtLabelKey is { } ctLabel)
            {
                Register(tab, tab.LabelKey, order, guest => guest.Kind == ResourceKind.Qemu);
                Register(tab, ctLabel, order, guest => guest.Kind == ResourceKind.Lxc);
            }
            else
            {
                Register(tab, tab.LabelKey, order, _ => true);
            }
        }

        // 복제 — 탭이 아니라 화면 하나(WPF 의 CloneWindow). 메인 화면의 복제 버튼이 이 항목을 연다.
        FeatureRegistry.GuestFeatures.Add(new GuestFeature("clone", "CloneWindow_01", "IconCopy", order + 10,
            (session, guest) => new CloneWindow(session.Api, guest)));
    }

    /// <summary>
    ///     탭 하나를 진입점으로 등록한다. 데스크톱은 탐색 창을 그 탭에 맞춰 열고(WPF 와 같다), 휴대폰은 "관리" 에서 누른 탭 화면을 바로 연다.
    ///     보이는 조건(권한)은 게스트 목록만 받으므로 관리자 권한으로 게스트 종류와 상태만 본다 — 권한에 없는 탭은 탐색 창에서 빠진다.
    /// </summary>
    private static void Register(GuestTab tab, string labelKey, int order, Func<PveResource, bool> kind)
    {
        FeatureRegistry.GuestFeatures.Add(new GuestFeature(tab.Id, labelKey, tab.IconKey, order,
            (session, guest) => PlatformInfo.IsDesktop
                ? GuestNavigator.Create(session, guest, tab.Id)
                : GuestNavigator.Page(session, guest, tab.Id) ?? GuestNavigator.Create(session, guest),
            guest => kind(guest) && tab.IsVisible(PermissionsInfo.Admin, guest)));
    }

    private static void RegisterPreviews()
    {
        const string area = "guest";
        foreach (var tab in GuestTabs.All)
        {
            var id = tab.Id;
            if (tab.IsVisible(PermissionsInfo.Admin, Sample(ResourceKind.Qemu)))
                PreviewRegistry.Add(area, new PreviewTab("vm-" + id, tab.LabelKey, tab.IconKey,
                    ctx => GuestNavigator.Page(ctx.Session, ctx.Vm, id)!));
            if (tab.IsVisible(PermissionsInfo.Admin, Sample(ResourceKind.Lxc)))
                PreviewRegistry.Add(area, new PreviewTab("ct-" + id, tab.CtLabelKey ?? tab.LabelKey, tab.IconKey,
                    ctx => GuestNavigator.Page(ctx.Session, ctx.Ct, id)!));
        }

        PreviewRegistry.Add(area, new PreviewTab("vm-clone", "CloneWindow_01", "IconCopy",
            ctx => new CloneWindow(ctx.Api, ctx.Vm)));
        PreviewRegistry.Add(area, new PreviewTab("ct-clone", "CloneWindow_01", "IconCopy",
            ctx => new CloneWindow(ctx.Api, ctx.Ct)));
    }

    /// <summary>탭이 보이는지 가늠할 가짜 게스트(종류만 맞춘다) — 미리보기 등록에만 쓴다.</summary>
    private static PveResource Sample(ResourceKind kind) => new()
    {
        Kind = kind, VmId = kind == ResourceKind.Qemu ? 100 : 200, Name = "sample", Node = "pve1"
    };
}

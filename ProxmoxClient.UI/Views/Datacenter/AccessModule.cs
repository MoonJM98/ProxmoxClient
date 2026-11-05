using System.Runtime.CompilerServices;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Datacenter;

/// <summary>
///     데이터센터 → 접근 제어 탭(사용자·2단계 인증·그룹·풀·역할·인증 영역·권한). 아직 탐색 창에 연결하지 않았으므로
///     검증 도구(target "preview:dc-access")로만 열어 본다. WPF DatacenterNavigator.AddAccess 와 같은 이름·아이콘.
/// </summary>
internal static class AccessModule
{
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255",
        Justification = "포팅 가이드가 정한 등록 방식 — 검증 도구가 모듈 로드 때 미리보기를 찾는다.")]
    internal static void Register()
    {
        PreviewRegistry.Add("dc-access", new PreviewTab("users", "DcTab_Users", "IconLogIn",
            ctx => DatacenterTables.Users(ctx.Api, canEdit: true)));
        PreviewRegistry.Add("dc-access", new PreviewTab("tfa", "DcTab_Tfa", "IconShield",
            ctx => DatacenterTables.Tfa(ctx.Api, canEdit: true)));
        PreviewRegistry.Add("dc-access", new PreviewTab("groups", "DcTab_Groups", "IconFolder",
            ctx => DatacenterTables.Groups(ctx.Api, canEdit: true)));
        PreviewRegistry.Add("dc-access", new PreviewTab("pools", "DcTab_Pools", "IconBox",
            ctx => DatacenterTables.Pools(ctx.Api, canEdit: true)));
        PreviewRegistry.Add("dc-access", new PreviewTab("roles", "DcTab_Roles", "IconKeyboard",
            ctx => DatacenterTables.Roles(ctx.Api, canEdit: true)));
        PreviewRegistry.Add("dc-access", new PreviewTab("realms", "DcTab_Realms", "IconServer",
            ctx => RealmSyncJobs.View(ctx.Api, canEdit: true, () => DatacenterTables.Realms(ctx.Api, canEdit: true))));
        PreviewRegistry.Add("dc-access", new PreviewTab("acl", "DcTab_Permissions", "IconCheck",
            ctx => DatacenterTables.Acl(ctx.Api, canEdit: true)));
    }
}

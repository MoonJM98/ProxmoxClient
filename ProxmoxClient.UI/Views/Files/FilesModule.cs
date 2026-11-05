using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Files;

/// <summary>
///     게스트 파일 기능의 진입점 등록 — 게스트 상세의 "관리" 목록 "파일"(WPF 콘솔의 [파일] 단추와 같은 이름·아이콘·순서).
///     검증용 미리보기(tools/UiSnapshot, target "preview:files")는 VM 100(게스트 에이전트)과 CT 200(노드 셸)을 연다.
/// </summary>
internal static class FilesModule
{
    // 가이드가 정한 등록 방식(진입점·검증 등록부)이라 라이브러리 경고(CA2255)는 의도적으로 끈다
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "진입점·검증 등록부 등록 — porting-guide 의 등록 방식")]
    internal static void Register()
    {
        FeatureRegistry.GuestFeatures.Add(new GuestFeature("files", "GuestFiles_Toggle", "IconFolder", 80,
            (session, guest) => GuestFilesWindow.Create(session.Api, guest, DefaultSftp(session.Api, guest))));

        const string area = "files";
        // 미리보기는 에이전트 읽기 허락 대화상자를 건너뛴다(검증 도구가 대화상자를 닫아 목록이 비므로)
        PreviewRegistry.Add(area, new PreviewTab("vm", "GuestFiles_Toggle", "IconFolder",
            ctx => GuestFilesWindow.Create(ctx.Api, ctx.Vm, skipAgentApproval: true)));
        PreviewRegistry.Add(area, new PreviewTab("ct", "GuestFiles_Toggle", "IconFolder",
            ctx => GuestFilesWindow.Create(ctx.Api, ctx.Ct, DefaultSftp(ctx.Api, ctx.Ct), skipAgentApproval: true)));
        // SFTP 접속·계정 설정과 SSH 키 설정 화면(페이지로 열리는 화면의 내용)
        PreviewRegistry.Add(area, new PreviewTab("sftp", "Sftp_Settings", "IconServer", _ => new SftpProfilesEditor(null, true)));
        PreviewRegistry.Add(area, new PreviewTab("ssh-key", "SshSetup_Title", "IconKey", _ => SshKeySetupWindow.Preview()));
    }

    /// <summary>
    ///     CT 는 노드 셸(root@pam 만)로 파일을 다루므로, root@pam 이 아니면 SFTP 가 기본이다(WPF TerminalWindow 와 같음).
    ///     VM 은 게스트 에이전트가 기본.
    /// </summary>
    private static bool DefaultSftp(ProxmoxApiClient api, PveResource guest) =>
        guest.Kind == ResourceKind.Lxc && !IsRootPam(api);

    private static bool IsRootPam(ProxmoxApiClient api)
    {
        var user = api.AuthenticatedUser ?? api.Profile.UserName;
        return api.Profile.AuthMode != AuthMode.ApiToken && api.AuthTicket is not null
               && string.Equals(user, "root@pam", StringComparison.OrdinalIgnoreCase);
    }
}

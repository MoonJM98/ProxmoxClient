using ProxmoxClient.Core.Files;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Views.Files;

/// <summary>
///     게스트 에이전트가 명령 실행(guest-exec)을 막아 두었을 때 — 게스트 OS 에 맞는 해제 명령을 보이고 복사·다시 연결하게 한다.
///     RHEL 계열은 설치 기본값(/etc/sysconfig/qemu-ga)이 막고, Windows 는 서비스 실행 인자(-b/-a)로 막는다.
///     에이전트 파일 쓰기가 열려 있어 해제 스크립트를 써 두었으면 그것을 실행하는 한 줄만 보인다(<see cref="AgentUnblock" />).
/// </summary>
public partial class GuestFilesPanel
{
    /// <summary>해제 안내를 보인다(목록 자리에).</summary>
    private void ShowUnblockGuide(GuestOsFamily family, string? scriptCommand)
    {
        _placeholder.IsVisible = false;
        _guidanceTitle.Text = Loc.T("GuestFiles_ExecBlockedTitle");
        _guidanceText.Text = Loc.T(family == GuestOsFamily.Windows
            ? "GuestFiles_UnblockWindows"
            : "GuestFiles_UnblockLinux");
        _guidanceCommands.Text = scriptCommand ?? AgentUnblock.Commands(family);
        _guidanceNote.Text = scriptCommand is not null
            ? Loc.T("GuestFiles_UnblockScript", AgentUnblock.Commands(family))
            : family switch
            {
                GuestOsFamily.Windows => string.Empty,
                GuestOsFamily.RedHat => Loc.T("GuestFiles_UnblockSelinux"),
                _ => Loc.T("GuestFiles_UnblockLinuxOther")
            };
        _guidanceNote.IsVisible = _guidanceNote.Text?.Length > 0;
        _guidance.IsVisible = true;
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Files;

namespace ProxmoxClient.App.Views.Shared;

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
        Placeholder.Visibility = Visibility.Collapsed;
        GuidanceTitle.Text = Loc.T("GuestFiles_ExecBlockedTitle");
        GuidanceText.Text = Loc.T(family == GuestOsFamily.Windows
            ? "GuestFiles_UnblockWindows"
            : "GuestFiles_UnblockLinux");
        GuidanceCommands.Text = scriptCommand ?? AgentUnblock.Commands(family);
        GuidanceNote.Text = scriptCommand is not null
            ? Loc.T("GuestFiles_UnblockScript", AgentUnblock.Commands(family))
            : family switch
            {
                GuestOsFamily.Windows => string.Empty,
                GuestOsFamily.RedHat => Loc.T("GuestFiles_UnblockSelinux"),
                _ => Loc.T("GuestFiles_UnblockLinuxOther")
            };
        GuidanceNote.Visibility = GuidanceNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Guidance.Visibility = Visibility.Visible;
    }

    private void OnCopyCommands(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(GuidanceCommands.Text.Replace("\n", Environment.NewLine));
            StatusText.Text = Loc.T("GuestFiles_Copied");
        }
        catch (ExternalException ex) // 다른 프로그램이 클립보드를 잡고 있을 때
        {
            StatusText.Text = Loc.T("GuestFiles_Error", ex.Message);
        }
    }

    /// <summary>다시 연결 — 게스트에서 설정을 푼 뒤.</summary>
    private async void OnRetryConnect(object sender, RoutedEventArgs e)
    {
        Guidance.Visibility = Visibility.Collapsed;
        await ShowAsync();
    }
}

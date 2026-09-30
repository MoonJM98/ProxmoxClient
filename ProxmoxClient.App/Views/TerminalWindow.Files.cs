using System.Windows;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Files;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.App.Views;

/// <summary>
///     CT 파일 창 — 도구 모음의 [파일] 로 따로 연다(이미 열려 있으면 앞으로).
///     root@pam(비밀번호 로그인)일 때만 보인다 — 보이지 않는 노드 셸(호스트 root)로 CT 파일에 접근하기 때문
///     (꺼진 CT 도 잠깐 마운트해 본다). 그 밖의 계정은 노드 셸을 로그인 없이 열 수 없고, PVE 의 CT 콘솔은 모든 연결이
///     한 화면(dtach)을 같이 써서 콘솔로 여는 방법은 보이는 터미널에 입력이 섞이므로 버튼을 두지 않는다.
/// </summary>
public partial class TerminalWindow
{
    private Func<Window, GuestFilesWindow>? _createFilesWindow;
    private GuestFilesWindow? _filesWindow;

    private void InitFiles(ProxmoxApiClient api, PveResource guest, string guestTitle)
    {
        if (!IsRootPam(api)) return; // 버튼은 숨긴 채로

        FilesSeparator.Visibility = BtnFiles.Visibility = Visibility.Visible;
        _createFilesWindow = owner =>
            GuestFilesWindow.Create(owner, guestTitle, ct => OpenNodeShellFilesAsync(api, guest, ct));
    }

    private static async Task<IGuestFileSystem> OpenNodeShellFilesAsync(ProxmoxApiClient api, PveResource guest,
        CancellationToken ct)
    {
        return await ContainerFileSystem.OpenAsync(api, guest.Node, guest.VmId, ct);
    }

    /// <summary>노드 셸이 로그인 없이 열리는 사용자 — root@pam 비밀번호(티켓) 로그인(서버가 알려 준 사용자 기준).</summary>
    internal static bool IsRootPam(ProxmoxApiClient api)
    {
        var user = api.AuthenticatedUser ?? api.Profile.UserName;
        return api.Profile.AuthMode != AuthMode.ApiToken && api.AuthTicket is not null
               && string.Equals(user, "root@pam", StringComparison.OrdinalIgnoreCase);
    }

    private void OnOpenFiles(object sender, RoutedEventArgs e)
    {
        if (_createFilesWindow is null) return;
        if (_filesWindow is null)
        {
            _filesWindow = _createFilesWindow(this);
            _filesWindow.Closed += (_, _) => _filesWindow = null;
            _filesWindow.Show();
            return;
        }

        if (_filesWindow.WindowState == WindowState.Minimized) _filesWindow.WindowState = WindowState.Normal;
        _filesWindow.Activate();
    }
}

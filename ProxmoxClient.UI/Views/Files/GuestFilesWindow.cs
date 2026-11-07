using Avalonia.Controls;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Files;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Files;

/// <summary>
///     게스트 파일 화면 — WPF GuestFilesWindow(콘솔 창에 딸린 창)를 화면 페이지로 옮긴 것. 화면에 붙으면 연결해 폴더를 보이고,
///     떠나면 연결을 정리한다. 내용은 <see cref="GuestFilesPanel" /> 과 같다.
/// </summary>
public sealed class GuestFilesWindow : UserControl
{
    private readonly GuestFilesPanel _panel = new();

    private GuestFilesWindow(ProxmoxApiClient api, PveResource guest, bool defaultSftp, bool skipAgentApproval)
    {
        var connections = new GuestFileConnections(api, guest, defaultSftp);
        _panel.Attach(async ct =>
        {
            // 게스트 에이전트 명령으로 읽는 것은 쓰기 확인 창이 막으므로, 화면을 열 때 한 번 허락받는다(쓰기는 따로 묻는다)
            if ((await connections.LoadAsync()).EffectiveTransport == GuestFileTransport.Agent && !skipAgentApproval)
            {
                if (!await Ask.ConfirmAsync(Loc.T(guest.Kind == ResourceKind.Lxc ? "GuestFiles_ContainerReadConfirm" : "GuestFiles_AgentReadConfirm", guest.Name), Loc.T("GuestFiles_Title")))
                    throw new OperationCanceledException(ct);
            }

            _panel.ApproveReads();
            return await connections.OpenAsync(ct);
        });
        Content = _panel;
        AttachedToVisualTree += async (_, _) => await _panel.ShowAsync();
        DetachedFromVisualTree += (_, _) => _panel.Close();
    }

    /// <summary>
    ///     파일 화면을 만든다. defaultSftp 이면 노드 셸을 못 쓰는 계정(CT, root@pam 아님)이라 SFTP 가 기본이다.
    ///     skipAgentApproval 은 검증 도구(미리보기)만 쓴다 — 도구가 대화상자를 자동으로 닫아 버리므로 허락을 건너뛴다.
    ///     제목(게스트 이름)은 여는 쪽이 화면 머리글로 붙인다.
    /// </summary>
    public static Control Create(ProxmoxApiClient api, PveResource guest, bool defaultSftp = false,
        bool skipAgentApproval = false) =>
        new GuestFilesWindow(api, guest, defaultSftp, skipAgentApproval);
}

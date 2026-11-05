using Avalonia.Controls;
using ProxmoxClient.Core.Files;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Files;

/// <summary>SFTP 접속 선택의 결과 — 고른 접속 프로필, 세션 동안만 가진 암호(없으면 null), 계정.</summary>
internal sealed record SftpSelection(Guid ProfileId, string? Password, SftpAccountProfile Account);

/// <summary>
///     SFTP 접속·계정 설정 화면 — WPF 의 SftpProfilesWindow(대화 창)를 화면 페이지로 연다. 제목 "SFTP 설정".
///     <see cref="ShowAsync" /> 는 "게스트에 쓰기" 로 고르면 결과를, 닫으면 null 을 준다.
/// </summary>
internal sealed class SftpProfilesWindow : UserControl
{
    private readonly TaskCompletionSource<SftpSelection?> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SftpProfilesEditor _editor;
    private SftpSelection? _selection;

    private SftpProfilesWindow(Guid? selectedProfileId)
    {
        _editor = new SftpProfilesEditor(selectedProfileId, allowConnect: true);
        _editor.ConnectionSelected += (_, _) =>
        {
            _selection = _editor.SelectedProfileId is { } id && _editor.SelectedAccount is { } account
                ? new SftpSelection(id, _editor.SelectedPassword, account)
                : null;
            Close();
        };
        _editor.CloseRequested += (_, _) => Close();
        Content = _editor;
        DetachedFromVisualTree += (_, _) => _done.TrySetResult(_selection);
    }

    /// <summary>설정 화면을 열고 닫힐 때까지 기다린다. 접속을 골랐으면 결과, 아니면 null.</summary>
    public static async Task<SftpSelection?> ShowAsync(Guid? selectedProfileId)
    {
        var view = new SftpProfilesWindow(selectedProfileId);
        await AppServices.Navigator.PushViewAsync(Loc.T("Sftp_Settings"), view);
        return await view._done.Task;
    }

    /// <summary>화면을 빼고 결과를 확정한다(뒤로 가기로 빠진 경우는 DetachedFromVisualTree 가 처리).</summary>
    private void Close()
    {
        if (_done.TrySetResult(_selection)) _ = AppServices.Navigator.PopAsync();
    }
}

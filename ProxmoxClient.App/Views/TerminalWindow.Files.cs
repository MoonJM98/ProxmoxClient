using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Files;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.App.Views;

/// <summary>
///     CT 파일 창 — 도구 모음의 [파일] 로 따로 연다(이미 열려 있으면 앞으로).
///     root@pam 비밀번호 로그인은 노드 셸과 SFTP 중 선택한다. 다른 계정은 직접 SFTP로 연결한다.
/// </summary>
public partial class TerminalWindow
{
    private Func<Window, GuestFilesWindow>? _createFilesWindow;
    private GuestFilesWindow? _filesWindow;
    private GuestFileConnections? _fileConnections;
    private GuestFileConnection? _filesConnection;

    private void InitFiles(ProxmoxApiClient api, PveResource guest, string guestTitle)
    {
        FilesSeparator.Visibility = BtnFiles.Visibility = Visibility.Visible;
        FilesNodeShell.IsEnabled = IsRootPam(api);
        _fileConnections = new GuestFileConnections(this, api, guest, !IsRootPam(api));
        _createFilesWindow = owner =>
            GuestFilesWindow.Create(owner, guestTitle, _fileConnections.OpenAsync);
        Loaded += async (_, _) => await UpdateFilesMenuAsync();
    }

    private async Task UpdateFilesMenuAsync()
    {
        if (_fileConnections is null) return;
        var connection = await _fileConnections.LoadAsync();
        ApplyFilesMenu(connection);
    }

    private void ApplyFilesMenu(GuestFileConnection connection)
    {
        var transport = connection.EffectiveTransport;
        FilesNodeShell.IsChecked = transport == GuestFileTransport.Agent;
        FilesSftp.IsChecked = transport == GuestFileTransport.Sftp;
        FilesSmb.IsChecked = transport == GuestFileTransport.Smb;
        FilesFtp.IsChecked = transport == GuestFileTransport.Ftp;
        BtnFiles.Content = Loc.T(ConsoleWindow.FilesButtonKey(transport, "Sftp_NodeFilesButton"));
        BtnFiles.ToolTip = Loc.T(transport == GuestFileTransport.Agent ? "GuestFiles_ToggleTip" : "Files_RemoteTip");
    }

    private async void OnFilesMethod(object sender, RoutedEventArgs e)
    {
        if (_fileConnections is null) return;
        try
        {
            var configured = ReferenceEquals(sender, FilesConfigure);
            var current = (await _fileConnections.LoadAsync()).EffectiveTransport;
            var transport = ConsoleWindow.MenuTransport(sender, FilesNodeShell, FilesSftp, FilesSmb, FilesConfigure,
                current);
            var selected = await _fileConnections.SelectAsync(transport, configured);
            if (selected || !configured) _filesWindow?.Close();
            await UpdateFilesMenuAsync();
            if (selected) await ShowFilesWindowAsync();
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this, ex.Message, Loc.T("Sftp_Connect"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>노드 셸이 로그인 없이 열리는 사용자 — root@pam 비밀번호(티켓) 로그인(서버가 알려 준 사용자 기준).</summary>
    internal static bool IsRootPam(ProxmoxApiClient api)
    {
        var user = api.AuthenticatedUser ?? api.Profile.UserName;
        return api.Profile.AuthMode != AuthMode.ApiToken && api.AuthTicket is not null
               && string.Equals(user, "root@pam", StringComparison.OrdinalIgnoreCase);
    }

    private async void OnOpenFiles(object sender, RoutedEventArgs e) => await ShowFilesWindowAsync();

    private async Task ShowFilesWindowAsync()
    {
        if (_createFilesWindow is null || _fileConnections is null) return;
        var connection = await _fileConnections.LoadAsync();
        if (_closed) return;
        ApplyFilesMenu(connection);
        if (_filesWindow is not null && _filesConnection != connection) _filesWindow.Close();
        if (_filesWindow is null)
        {
            _filesConnection = connection;
            _filesWindow = _createFilesWindow(this);
            _filesWindow.Closed += (_, _) => { _filesWindow = null; _filesConnection = null; };
            _filesWindow.Show();
            return;
        }

        if (_filesWindow.WindowState == WindowState.Minimized) _filesWindow.WindowState = WindowState.Normal;
        _filesWindow.Activate();
    }
}

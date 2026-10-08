using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Files;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views;

/// <summary>
///     VM 파일 창 — 도구 모음의 [파일] 로 따로 연다(게스트 에이전트). 콘솔 화면에 PC 파일을 끌어다 놓아도
///     파일 창을 열고 지금 폴더로 올린다. 파일 창이 앞에 있는 동안 이 창은 비활성이라 키는 게스트로 가지 않는다.
/// </summary>
public partial class ConsoleWindow
{
    private Func<CancellationToken, Task<IGuestFileSystem>>? _openFiles;
    private GuestFilesWindow? _filesWindow;
    private GuestFileConnections? _fileConnections;
    private GuestFileConnection? _filesConnection;

    private void InitFiles(ProxmoxApiClient api, PveResource guest)
    {
        _fileConnections = new GuestFileConnections(this, api, guest);
        _openFiles = _fileConnections.OpenAsync;
        Loaded += async (_, _) => await UpdateFilesMenuAsync();
    }

    private async void OnOpenFiles(object sender, RoutedEventArgs e) => await ShowFilesWindowAsync();

    private async Task UpdateFilesMenuAsync()
    {
        if (_fileConnections is null) return;
        var connection = await _fileConnections.LoadAsync();
        ApplyFilesMenu(connection);
    }

    private void ApplyFilesMenu(GuestFileConnection connection)
    {
        var transport = connection.EffectiveTransport;
        FilesAgent.IsChecked = transport == GuestFileTransport.Agent;
        FilesSftp.IsChecked = transport == GuestFileTransport.Sftp;
        FilesSmb.IsChecked = transport == GuestFileTransport.Smb;
        FilesFtp.IsChecked = transport == GuestFileTransport.Ftp;
        BtnFiles.Content = Loc.T(FilesButtonKey(transport, "Sftp_AgentFilesButton"));
        BtnFiles.ToolTip = Loc.T(transport == GuestFileTransport.Agent ? "GuestFiles_ToggleTipVm" : "Files_RemoteTip");
    }

    /// <summary>[파일] 버튼 글 — 고른 방식을 괄호로(에이전트·노드 셸은 창마다 다른 글).</summary>
    internal static string FilesButtonKey(GuestFileTransport transport, string agentKey) => transport switch
    {
        GuestFileTransport.Sftp => "Sftp_FilesButton",
        GuestFileTransport.Smb => "Files_SmbButton",
        GuestFileTransport.Ftp => "Files_FtpButton",
        _ => agentKey
    };

    /// <summary>메뉴에서 고른 방식 — 연결 설정은 지금 방식의 설정(에이전트면 SFTP 설정, 전과 같이).</summary>
    internal static GuestFileTransport MenuTransport(object sender, object agentItem, object sftpItem, object smbItem,
        object configureItem, GuestFileTransport current)
    {
        if (ReferenceEquals(sender, configureItem))
            return current == GuestFileTransport.Agent ? GuestFileTransport.Sftp : current;
        if (ReferenceEquals(sender, agentItem)) return GuestFileTransport.Agent;
        if (ReferenceEquals(sender, sftpItem)) return GuestFileTransport.Sftp;
        return ReferenceEquals(sender, smbItem) ? GuestFileTransport.Smb : GuestFileTransport.Ftp;
    }

    private async void OnFilesMethod(object sender, RoutedEventArgs e)
    {
        if (_fileConnections is null) return;
        try
        {
            var configured = ReferenceEquals(sender, FilesConfigure);
            var current = (await _fileConnections.LoadAsync()).EffectiveTransport;
            var transport = MenuTransport(sender, FilesAgent, FilesSftp, FilesSmb, FilesConfigure, current);
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

    /// <summary>파일 창을 연다 — 이미 열려 있으면 앞으로 가져온다.</summary>
    private async Task<GuestFilesWindow?> ShowFilesWindowAsync()
    {
        if (_openFiles is null || _fileConnections is null) return null;
        var connection = await _fileConnections.LoadAsync();
        if (_closed) return null;
        ApplyFilesMenu(connection);
        if (_filesWindow is not null && _filesConnection != connection) _filesWindow.Close();
        if (_filesWindow is null)
        {
            _filesConnection = connection;
            _filesWindow = GuestFilesWindow.Create(this, _guestTitle, _openFiles);
            _filesWindow.Closed += (_, _) => { _filesWindow = null; _filesConnection = null; };
            _filesWindow.Show();
        }
        else
        {
            if (_filesWindow.WindowState == WindowState.Minimized) _filesWindow.WindowState = WindowState.Normal;
            _filesWindow.Activate();
        }

        return _filesWindow;
    }

    private void OnScreenDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _openFiles is not null && !GuestFilesPanel.IsDraggingOut
                                           && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>콘솔 화면에 놓은 파일 — 파일 창을 열고(처음이면 연결) 지금 폴더로 올린다.</summary>
    private async void OnScreenDrop(object sender, DragEventArgs e)
    {
        if (GuestFilesPanel.IsDraggingOut || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        e.Handled = true;
        if (await ShowFilesWindowAsync() is { } window) await window.UploadAsync(paths);
    }
}

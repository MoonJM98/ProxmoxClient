using System.Windows;
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

    private void InitFiles(ProxmoxApiClient api, PveResource guest)
    {
        _openFiles = ct => OpenAgentFilesAsync(api, guest, ct);
    }

    private static async Task<IGuestFileSystem> OpenAgentFilesAsync(ProxmoxApiClient api, PveResource guest,
        CancellationToken ct)
    {
        return await AgentFileSystem.OpenAsync(api, guest.Node, guest.VmId, ct);
    }

    private void OnOpenFiles(object sender, RoutedEventArgs e) => ShowFilesWindow();

    /// <summary>파일 창을 연다 — 이미 열려 있으면 앞으로 가져온다.</summary>
    private GuestFilesWindow? ShowFilesWindow()
    {
        if (_openFiles is null) return null;
        if (_filesWindow is null)
        {
            _filesWindow = GuestFilesWindow.Create(this, _guestTitle, _openFiles);
            _filesWindow.Closed += (_, _) => _filesWindow = null;
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
        if (ShowFilesWindow() is { } window) await window.UploadAsync(paths);
    }
}

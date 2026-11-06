using System.IO;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Storage;

/// <summary>
///     저장소 창 — 탭은 공용 <see cref="StorageTabs" />. 여기에는 창과, 이 앱의 올리기(파일 열기 창 · 진행 창)만 둔다.
/// </summary>
public static class StorageNavigator
{
    /// <param name="pluginType">저장소 유형(pbs 면 백업 속 파일 복원) — 모르면 null(버튼을 두고 눌렀을 때 판단).</param>
    public static NavWindow Create(ProxmoxApiClient api, string node, string storage, string content,
        PermissionsInfo permissions, string? pluginType = null)
    {
        var tabs = StorageTabs.Tabs(api, node, storage, content, permissions, pluginType).Select(ViewSpecs.Tab)
            .ToList();
        return new NavWindow(Loc.T("StorageWindow_Title", storage, node), Loc.T("StorageWindow_Title", storage, node),
            "IconDatabase", tabs, null);
    }

    /// <summary>파일을 고르고 진행 창을 띄워 올린 뒤, 서버 작업이 끝나면 결과를 돌려준다(취소면 취소 문구).</summary>
    public static async Task<string?> UploadAsync(ProxmoxApiClient api, string node, string storage, string content,
        object? owner)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.T(content == "iso" ? "StorageUpload_IsoFilter" : "StorageUpload_TemplateFilter")
        };
        if (dialog.ShowDialog(owner as Window) != true) return null;

        var progress = new UploadProgressWindow(Path.GetFileName(dialog.FileName)) { Owner = owner as Window };
        progress.Show();
        try
        {
            var upid = await api.UploadToStorageAsync(node, storage, content, dialog.FileName, progress.Progress,
                progress.Token);
            progress.Close();
            var status = upid.Length == 0 ? "OK" : (await api.WaitTaskAsync(upid)).Status;
            return Loc.T("StorageUpload_Done", status);
        }
        catch (OperationCanceledException)
        {
            return Loc.T("StorageUpload_Cancelled");
        }
        finally
        {
            if (progress.IsVisible) progress.Close();
        }
    }
}

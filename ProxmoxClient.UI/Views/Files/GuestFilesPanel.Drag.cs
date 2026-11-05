using Avalonia.Input;
using Avalonia.Platform.Storage;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Views.Files;

/// <summary>
///     PC 에서 파일을 끌어다 놓으면 지금 폴더로 올린다(데스크톱·태블릿 — 휴대폰은 올리기 단추의 선택기를 쓴다).
///     WPF 의 "목록에서 PC 로 끌어내기"(Windows 가상 파일 데이터 객체)는 옮기지 않았다 — 받기 단추의 "다른 곳에 저장"으로 대신한다.
/// </summary>
public partial class GuestFilesPanel
{
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var accepts = _files is not null && _busy is null && e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { } items) return;

        e.Handled = true;
        if (_files is null)
        {
            _status.Text = Loc.T("GuestFiles_NotReady");
            return;
        }

        await UploadItemsAsync(items.OfType<IStorageFile>().ToList());
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     오른쪽 클릭 메뉴와 숨김 파일 보기 — 숨김·시스템 항목은 켜면 흐리게 보이고, 끄면 목록에서 뺀다(설정에 기억).
/// </summary>
public partial class GuestFilesPanel
{
    private bool _showHidden = true;
    private bool _hiddenTouched; // 설정을 읽기 전에 사용자가 바꿨으면 읽은 값으로 덮지 않는다

    /// <summary>숨김 파일 보기 설정을 읽어 둔다(없거나 못 읽으면 보임).</summary>
    private async Task LoadViewSettingsAsync()
    {
        var settings = await new ConsoleSettingsStore().LoadAsync();
        if (!_hiddenTouched) SetShowHidden(settings.ShowHiddenGuestFiles);
    }

    private async void OnToggleHidden(object sender, RoutedEventArgs e)
    {
        _hiddenTouched = true;
        SetShowHidden(sender is MenuItem ? !_showHidden : BtnHidden.IsChecked == true);
        try
        {
            // 다른 창이 바꾼 콘솔 설정을 덮지 않게 저장 직전에 다시 읽어 이 값만 바꾼다
            var store = new ConsoleSettingsStore();
            var settings = await store.LoadAsync();
            await store.SaveAsync(settings with { ShowHiddenGuestFiles = _showHidden });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            App.Log($"[게스트 파일] 숨김 파일 설정 저장 실패: {ex.Message}");
        }
    }

    private void SetShowHidden(bool show)
    {
        _showHidden = show;
        BtnHidden.IsChecked = show;
        IconAssist.SetIcon(MenuHidden, show ? (Geometry)FindResource("IconCheck") : null);
        if (_lastEntries is { } entries) ShowEntries(_current, entries);
    }

    /// <summary>오른쪽 클릭 — 선택 밖의 줄이면 그 줄만 고르고, 빈 곳이면 선택을 푼다(메뉴가 가리키는 대상).</summary>
    private void OnGridPreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not DataGridRow) source = VisualTreeHelper.GetParent(source);
        if (source is DataGridRow { Item: GuestFileRow row })
        {
            if (FileGrid.SelectedItems.Contains(row)) return;

            FileGrid.SelectedItems.Clear();
            FileGrid.SelectedItem = row;
            return;
        }

        FileGrid.SelectedItems.Clear();
    }

    /// <summary>메뉴를 열 때 — 지금 선택·연결 상태에 맞게 항목을 켜고 끈다.</summary>
    private void OnGridMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var ready = _files is not null && _busy is null;
        var count = SelectedRows.Count;
        MenuOpen.IsEnabled = ready && count == 1;
        MenuDownload.IsEnabled = ready && count > 0;
        MenuCopyPath.IsEnabled = _files is not null;
        MenuRename.IsEnabled = ready && count == 1;
        MenuDelete.IsEnabled = ready && count > 0;
        MenuUpload.IsEnabled = MenuNewFolder.IsEnabled = MenuRefresh.IsEnabled = ready;
        MenuHidden.IsEnabled = true;
    }

    private async void OnMenuOpen(object sender, RoutedEventArgs e)
    {
        if (_files is not null && SelectedRow is { } row) await OpenAsync(row);
    }

    /// <summary>경로 복사 — 고른 항목들의 게스트 경로(줄마다 하나), 없으면 지금 폴더.</summary>
    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (_files is not { } files) return;

        var rows = SelectedRows;
        var text = rows.Count == 0
            ? _current
            : string.Join(Environment.NewLine, rows.Select(r => files.Combine(_current, r.Entry.Name)));
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException ex) // 다른 프로그램이 클립보드를 잡고 있을 때
        {
            StatusText.Text = Loc.T("GuestFiles_Error", ex.Message);
        }
    }
}

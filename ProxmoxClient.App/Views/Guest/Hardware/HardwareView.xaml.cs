using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>
///     VM 하드웨어 화면 — 웹 UI(qemu/HardwareView.js)와 같은 목록·도구 모음·편집 창.
///     …/pending 으로 읽어 재시작 후 적용될 값을 따로 보여 주고, 되돌리기(revert)를 할 수 있다.
/// </summary>
public partial class HardwareView : UserControl
{
    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private readonly bool _isCt;
    private bool _busy;
    private HardwareContext? _ctx;

    public HardwareView(ProxmoxApiClient api, PveResource guest)
    {
        InitializeComponent();
        _api = api;
        _guest = guest;
        _isCt = guest.Kind == ResourceKind.Lxc;
        // CT 는 웹 UI 처럼 "볼륨 작업" — EFI 인증서 메뉴는 없다
        if (_isCt) BtnDisk.Content = Loc.T("Ct_VolumeMenu");
        Loaded += async (_, _) => await ReloadAsync();
    }

    private HardwareRow? Selected => RowGrid.SelectedItem as HardwareRow;

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        await ReloadAsync();
    }

    /// <summary>설정·대기 중 변경·실행 상태를 다시 읽는다. 상태 줄은 건드리지 않는다(방금 한 작업 결과가 남는다).</summary>
    private async Task ReloadAsync()
    {
        try
        {
            var config = await _api.GetGuestPendingAsync(_guest.Node, _guest.Kind, _guest.VmId);
            var running = await IsRunningAsync();
            _ctx = new HardwareContext(_api, _guest, config, running);
            var selectedKey = Selected?.Key;
            var rows = _isCt ? HardwareRows.BuildCt(config) : HardwareRows.Build(config);
            RowGrid.ItemsSource = rows;
            RowGrid.SelectedItem = rows.FirstOrDefault(r => r.Key == selectedKey);
            PendingColumn.Visibility = rows.Any(r => r.HasPending) ? Visibility.Visible : Visibility.Collapsed;
            BuildAddMenu();
            UpdateButtons();
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("GuestSettingsWindow_M03", ex.Message);
            UpdateButtons(); // 다시 읽기에 실패해도 버튼이 꺼진 채로 남지 않게
        }
    }

    private async Task<bool> IsRunningAsync()
    {
        try
        {
            return await _api.GetGuestCurrentStatusAsync(_guest.Node, _guest.Kind, _guest.VmId) == "running";
        }
        catch (ProxmoxApiException ex)
        {
            App.Log($"[하드웨어] {_guest.VmId} 상태 조회 실패: {ex.Message}");
            return false;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
    }

    /// <summary>웹 UI 와 같은 버튼 규칙 — 디스크는 "분리", 대기 중인 줄은 디스크 작업 불가, 되돌리기는 대기 중일 때만.</summary>
    private void UpdateButtons()
    {
        var row = Selected;
        BtnEdit.IsEnabled = !_busy && row is { HasEditor: true };
        BtnRemove.IsEnabled = !_busy && row is { CanRemove: true };
        BtnRemove.Content = Loc.T(row is { IsUsedDisk: true } ? "Hw_Detach" : "Hw_Remove");
        BtnRevert.IsEnabled = !_busy && row is { HasPending: true };

        var diskLike = row is { IsDisk: true } || row?.Item == HardwareItem.Efi
                       || row?.Item == HardwareItem.Tpm && _ctx is { IsRunning: false };
        BtnDisk.IsEnabled = !_busy && row is { HasPending: false, IsDeleted: false } && diskLike;
        // CT: 미사용 볼륨은 이동 불가, 루트 디스크는 소유자 변경 불가(웹 UI 와 같이)
        MenuMove.IsEnabled = BtnDisk.IsEnabled && !(_isCt && row?.Item == HardwareItem.Unused);
        MenuReassign.IsEnabled = BtnDisk.IsEnabled
                                 && row?.Item is HardwareItem.Disk or HardwareItem.Unused or HardwareItem.MountPoint;
        MenuResize.IsEnabled = BtnDisk.IsEnabled && row is { IsUsedDisk: true };
        MenuEnroll.Visibility = row?.Item == HardwareItem.Efi ? Visibility.Visible : Visibility.Collapsed;
        MenuEnroll.IsEnabled = BtnDisk.IsEnabled && _ctx is not null && DiskActions.CanEnrollCertificates(_ctx);
        BtnAdd.IsEnabled = !_busy;
    }

    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is HardwareRow { HasEditor: true }) OnEdit(sender, e);
    }

    // ------------------------------------------------------------ 제거 · 되돌리기

    /// <summary>
    ///     제거/분리 — 디스크를 분리하면 미사용 디스크로 남고(데이터 보존), 미사용 디스크를 지우면 이미지가 영구 삭제된다
    ///     (그래서 이름을 직접 입력해야 한다). 미사용 디스크나 실행 중 VM 의 디스크는 웹 UI 처럼 백그라운드 작업으로 보낸다.
    /// </summary>
    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_busy || _ctx is null || Selected is not { CanRemove: true } row) return;

        // 미사용 디스크·최대 절전 상태는 지우면 되돌릴 수 없다 — 이름을 직접 입력해야 한다
        var destroys = row.Item is HardwareItem.Unused or HardwareItem.VmState;
        var confirm = new FormDialog(
            Loc.T(row.IsUsedDisk ? "Hw_DetachConfirm" : destroys ? "Hw_DestroyConfirm" : "Hw_RemoveConfirm",
                row.Header),
            destroys ? [ActionHelpers.TypeToConfirmField()] : [],
            destroys ? ActionHelpers.TypedMatches(row.Key) : null) { Owner = Window.GetWindow(this) };
        if (confirm.ShowDialog() != true) return;

        // CT 는 웹 UI 처럼 늘 PUT, VM 은 미사용 디스크·실행 중 디스크를 백그라운드 작업으로
        var background = !_isCt && (destroys || (row.IsDisk || row.Item == HardwareItem.CloudInit) && _ctx.IsRunning);
        await SendAsync(new Dictionary<string, string> { [row.Key] = string.Empty }, background,
            Loc.T(row.IsUsedDisk ? "DeviceTable_Detached" : "Hw_Removed", "OK"));
    }

    private async void OnRevert(object sender, RoutedEventArgs e)
    {
        if (_busy || _ctx is null || Selected is not { HasPending: true } row) return;

        var keys = row.Keys.Where(_ctx.Config.PendingKeys.Contains).ToList();
        await RunAsync(async () =>
        {
            await _api.RevertGuestPendingAsync(_guest.Node, _guest.Kind, _guest.VmId, keys);
            return Loc.T("OptionsTab_Reverted", row.Header);
        });
    }

    // ------------------------------------------------------------ 보내기

    /// <summary>
    ///     설정 변경을 보낸다 — 빈 값은 delete 로. background 면 웹 UI 처럼 POST …/config + background_delay=5 로 보내고
    ///     생긴 작업(UPID)이 끝날 때까지 기다린다.
    /// </summary>
    private Task SendAsync(IReadOnlyDictionary<string, string> changes, bool background, string doneText)
    {
        var form = ActionHelpers.UpdateForm(changes);
        return RunAsync(async () =>
        {
            if (!background)
            {
                await _api.UpdateGuestConfigAsync(_guest.Node, _guest.Kind, _guest.VmId, form);
                return doneText;
            }

            form["background_delay"] = "5";
            var status = await ActionHelpers.RunTaskAsync(_api, _api.PostActionAsync($"{_ctx!.GuestPath}/config", form),
                "Hw_TaskDone");
            return $"{doneText} ({status})";
        });
    }

    /// <summary>작업을 실행하고 다시 읽는다 — 결과 문구는 다시 읽은 뒤에도 남는다.</summary>
    private async Task RunAsync(Func<Task<string>> work)
    {
        _busy = true;
        UpdateButtons();
        try
        {
            StatusText.Text = Loc.T("GuestSettingsWindow_M05", 1);
            StatusText.Text = await work();
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("AppSettingsWindow_M03", ex.Message);
            App.Log($"[하드웨어] {_guest.VmId} 작업 실패: {ex.Message}");
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }

        await ReloadAsync();
    }

    /// <summary>
    ///     편집 창을 띄우고 확인하면 바뀐 설정만 보낸다(대기 중 값 기준으로 같은 값은 빼고).
    /// </summary>
    private async Task ApplyEditAsync(HardwareEdit edit)
    {
        var dialog = new FormDialog(edit.Title, edit.Fields, edit.Validate) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } values || _ctx is null) return;

        var changes = edit.Build(values)
            .Where(kv => _ctx.Effective.GetValueOrDefault(kv.Key, "") != kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (changes.Count == 0)
        {
            StatusText.Text = Loc.T("GuestSettingsWindow_M04");
            return;
        }

        await SendAsync(changes, edit.Background, Loc.T("GuestSettingsWindow_M06"));
    }

    /// <summary>디스크 작업(move_disk·resize)을 보내고 작업이 끝날 때까지 기다린다.</summary>
    private async Task ApplyDiskActionAsync(HardwareEdit edit,
        Func<IReadOnlyDictionary<string, string>, DiskActions.DiskRequest> request)
    {
        var dialog = new FormDialog(edit.Title, edit.Fields, edit.Validate) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } values) return;

        var r = request(values);
        await RunAsync(() => ActionHelpers.RunTaskAsync(_api,
            r.Method == HttpMethod.Put ? _api.PutActionAsync(r.Path, r.Form) : _api.PostActionAsync(r.Path, r.Form),
            "Hw_TaskDone"));
    }
}

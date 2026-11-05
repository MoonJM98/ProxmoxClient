using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Converters;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Controls;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Guest.Hardware;

/// <summary>
///     VM·CT 하드웨어 화면 — WPF HardwareView 와 같은 도구 모음·표(DataGrid)·편집 창(웹 UI qemu/HardwareView.js).
///     데스크톱은 WPF 와 같은 열(항목 220 · 값 · 대기 중 값)의 표를 쓴다. 모바일은 줄마다 "항목 / 값" 카드로 바꾼다.
///     …/pending 으로 읽어 재시작 후 적용될 값을 주황색 열·줄로 따로 보여 주고, 되돌리기(revert)를 할 수 있다.
///     (메뉴·편집·디스크 작업은 HardwareView.Menus.cs)
/// </summary>
public partial class HardwareView : UserControl
{
    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private readonly bool _isCt;

    /// <summary>별 폭 열의 최소 폭 — 표 창(TableTab)과 같이, 값 열이 한 글자 폭으로 눌리지 않게 한다.</summary>
    private const double StarMinWidth = 140;

    /// <summary>WPF 의 항목 열 폭(고정 220).</summary>
    private const double NameColumnWidth = 220;

    private readonly DataGrid? _grid;
    private readonly DataGridTextColumn? _valueColumn;
    private readonly DataGridTextColumn? _pendingColumn;
    private readonly ListBox? _cards;
    private readonly TextBlock _status = new()
    {
        Classes = { "DialogStatus" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0)
    };
    private readonly Button _add;
    private readonly Button _remove;
    private readonly Button _edit;
    private readonly Button _disk;
    private readonly Button _revert;
    private readonly MenuFlyout _addMenu = new() { Placement = PlacementMode.Bottom };
    private readonly MenuFlyout _diskMenu = new() { Placement = PlacementMode.Bottom };
    private readonly MenuItem _menuMove;
    private readonly MenuItem _menuReassign;
    private readonly MenuItem _menuResize;
    private readonly MenuItem _menuEnroll;
    private bool _busy;
    private bool _loaded;
    private HardwareContext? _ctx;

    public HardwareView(ProxmoxApiClient api, PveResource guest)
    {
        _api = api;
        _guest = guest;
        _isCt = guest.Kind == ResourceKind.Lxc;

        // 웹 UI 하드웨어 도구 모음(WPF 와 같은 순서·아이콘): 추가(메뉴) · 제거(분리) · 편집 · 디스크 작업(메뉴) · 되돌리기 · 새로 고침
        _add = Tool(Loc.T("Hw_AddMenu"), "IconPlus", dropdown: true);
        _add.Flyout = _addMenu;

        _remove = Tool(Loc.T("Hw_Remove"), "IconTrash");
        _remove.Click += async (_, _) => await OnRemoveAsync();

        _edit = Tool(Loc.T("ServerManagerWindow_03"), "IconPencil");
        _edit.Click += async (_, _) => await OnEditAsync();

        // CT 는 웹 UI 처럼 "볼륨 작업" — EFI 인증서 메뉴는 없다
        _disk = Tool(Loc.T(_isCt ? "Ct_VolumeMenu" : "Hw_DiskMenu"), "IconDatabase", dropdown: true);
        _disk.Flyout = _diskMenu;

        _menuMove = DiskMenuItem("Hw_DiskMove", () => OnMoveDiskAsync());
        _menuReassign = DiskMenuItem("Hw_DiskReassign", () => OnReassignDiskAsync());
        _menuResize = DiskMenuItem("Hw_DiskResize", () => OnResizeDiskAsync());
        _menuEnroll = DiskMenuItem("Hw_DiskEnroll", () => OnEnrollCertsAsync());

        _revert = Tool(Loc.T("OptionsTab_Revert"), "IconUndo");
        _revert.Click += async (_, _) => await OnRevertAsync();

        var refresh = Tool(Loc.T("MainWindow_03"), "IconRefresh");
        refresh.Click += async (_, _) =>
        {
            _status.Text = string.Empty;
            await ReloadAsync();
        };

        var toolbar = new WrapPanel
        {
            Margin = new Thickness(0, 0, 0, 6),
            Children = { _add, _remove, _edit, _disk, _revert, refresh }
        };

        Control body;
        if (PlatformInfo.IsDesktop)
        {
            // WPF: 대기 중 값 열 — 글자색은 BrushWarn(주황). 값은 PendingText 를 그대로 보인다.
            _pendingColumn = new DataGridTextColumn
            {
                Header = Loc.T("OptionsTab_Pending"), Binding = new Binding("PendingText") { Mode = BindingMode.OneWay },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = StarMinWidth,
                Foreground = HardwareInput.Brush("BrushWarn")
            };
            _grid = new DataGrid
            {
                AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HeadersVisibility = DataGridHeadersVisibility.Column
            };
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = Loc.T("MainWindow_40"), Binding = new Binding("Header") { Mode = BindingMode.OneWay },
                Width = new DataGridLength(220)
            });
            _valueColumn = new DataGridTextColumn
            {
                Header = Loc.T("DeviceTable_Value"), Binding = new Binding("Display") { Mode = BindingMode.OneWay },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = StarMinWidth
            };
            _grid.Columns.Add(_valueColumn);
            _grid.SizeChanged += (_, e) => FitValueColumns(e.NewSize.Width);
            _grid.SelectionChanged += (_, _) => UpdateButtons();
            // WPF: 줄을 더블클릭하면 편집 창(머리글 더블클릭은 무시)
            _grid.DoubleTapped += async (_, e) =>
            {
                if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(true)?.DataContext
                    is HardwareRow { HasEditor: true })
                    await OnEditAsync();
            };
            body = _grid;
        }
        else
        {
            _cards = new ListBox { Background = Brushes.Transparent, ItemTemplate = CardTemplate() };
            _cards.SelectionChanged += (_, _) => UpdateButtons();
            _cards.DoubleTapped += async (_, _) => await OnEditAsync();
            body = _cards;
        }

        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"),
            Margin = PlatformInfo.IsDesktop ? new Thickness(20, 16) : new Thickness(12)
        };
        root.Children.Add(toolbar);
        root.Children.Add(body);
        root.Children.Add(_status);
        Grid.SetRow(body, 1);
        Grid.SetRow(_status, 2);
        Content = root;

        UpdateButtons();
        AttachedToVisualTree += async (_, _) =>
        {
            if (_loaded) return;

            _loaded = true;
            await ReloadAsync();
        };
    }

    private HardwareRow? Selected => (_grid?.SelectedItem ?? _cards?.SelectedItem) as HardwareRow;

    /// <summary>도구 모음 단추 — WPF 와 같은 여백·최소 폭, 드롭다운이면 ▾ 표시.</summary>
    private static Button Tool(string label, string iconKey, bool dropdown = false)
    {
        var button = HardwareInput.Button(label, iconKey);
        button.Margin = new Thickness(0, 0, 8, 4);
        IconAssist.SetDropdown(button, dropdown);
        return button;
    }

    private void SetPendingColumn(bool show)
    {
        if (_grid is null || _pendingColumn is null || _valueColumn is null) return;

        if (show && !_grid.Columns.Contains(_pendingColumn))
        {
            _grid.Columns.Add(_pendingColumn);
            FitValueColumns(_grid.Bounds.Width);
        }
        else if (!show && _grid.Columns.Contains(_pendingColumn))
        {
            _grid.Columns.Remove(_pendingColumn);
            _valueColumn.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        }
    }

    /// <summary>
    ///     대기 중 값 열이 보일 때 WPF 의 "*" "*" 처럼 값 열 둘이 남은 폭을 똑같이 나눈다(이름 열 220 제외).
    ///     Avalonia 의 별 폭 배분은 내용에 따라 한쪽으로 쏠리므로 픽셀 폭으로 맞춘다.
    /// </summary>
    private void FitValueColumns(double gridWidth)
    {
        if (_grid is null || _valueColumn is null || _pendingColumn is null || !_grid.Columns.Contains(_pendingColumn))
            return;

        var half = Math.Max(StarMinWidth, Math.Floor((gridWidth - NameColumnWidth - 4) / 2));
        _valueColumn.Width = new DataGridLength(half);
        _pendingColumn.Width = new DataGridLength(half);
    }

    private MenuItem DiskMenuItem(string labelKey, Func<Task> run)
    {
        var item = new MenuItem { Header = Loc.T(labelKey) };
        item.Click += async (_, _) => await run();
        _diskMenu.Items.Add(item);
        return item;
    }

    /// <summary>모바일 줄 한 개 — 항목 제목, 아래 값(대기 중 값은 주황색 줄).</summary>
    private static IDataTemplate CardTemplate()
    {
        return new FuncDataTemplate<HardwareRow>((row, _) => BuildCard(row), supportsRecycling: false);
    }

    private static Control BuildCard(HardwareRow? row)
    {
        var panel = new Grid { Margin = new Thickness(0, 6), RowSpacing = 2 };
        if (row is null) return panel;

        var value = new StackPanel { Spacing = 2 };
        value.Children.Add(new TextBlock { Text = row.Display, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
        if (row.PendingText.Length > 0)
            value.Children.Add(new TextBlock
            {
                Text = row.PendingText, Foreground = Conv.WarnBrush, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None, FontSize = 13
            });

        var title = new TextBlock
        {
            Text = row.Header, Classes = { "title" }, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None, FontSize = 15
        };
        panel.RowDefinitions = new RowDefinitions("Auto,Auto");
        Grid.SetRow(title, 0);
        Grid.SetRow(value, 1);
        panel.Children.Add(title);
        panel.Children.Add(value);
        return panel;
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
            if (_grid is not null)
            {
                _grid.ItemsSource = rows;
                _grid.SelectedItem = rows.FirstOrDefault(r => r.Key == selectedKey);
                // WPF: 대기 중 값이 하나도 없으면 그 열은 숨긴다(열을 넣고 빼서 두 값 열이 같은 폭을 나눈다)
                SetPendingColumn(rows.Any(r => r.HasPending));
            }
            else if (_cards is not null)
            {
                _cards.ItemsSource = rows;
                _cards.SelectedItem = rows.FirstOrDefault(r => r.Key == selectedKey);
            }

            BuildAddMenu(rows);
            UpdateButtons();
        }
        catch (Exception ex)
        {
            _status.Text = Loc.T("GuestSettingsWindow_M03", ex.Message);
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
            HardwareLog.Warn($"[하드웨어] {_guest.VmId} 상태 조회 실패: {ex.Message}");
            return false;
        }
    }

    /// <summary>웹 UI 와 같은 버튼 규칙 — 디스크는 "분리", 대기 중인 줄은 디스크 작업 불가, 되돌리기는 대기 중일 때만.</summary>
    private void UpdateButtons()
    {
        var row = Selected;
        _edit.IsEnabled = !_busy && row is { HasEditor: true };
        _remove.IsEnabled = !_busy && row is { CanRemove: true };
        _remove.Content = Loc.T(row is { IsUsedDisk: true } ? "Hw_Detach" : "Hw_Remove");
        _revert.IsEnabled = !_busy && row is { HasPending: true };

        var diskLike = row is { IsDisk: true } || row?.Item == HardwareItem.Efi
                       || row?.Item == HardwareItem.Tpm && _ctx is { IsRunning: false };
        _disk.IsEnabled = !_busy && row is { HasPending: false, IsDeleted: false } && diskLike;
        // CT: 미사용 볼륨은 이동 불가, 루트 디스크는 소유자 변경 불가(웹 UI 와 같이)
        _menuMove.IsEnabled = _disk.IsEnabled && !(_isCt && row?.Item == HardwareItem.Unused);
        // 다른 게스트에 넘기기(target-vmid)는 7.1+
        _menuReassign.IsEnabled = _disk.IsEnabled
                                  && _api.Guests.Feature(nameof(GuestsApi.MoveDiskAsync), "target-vmid").IsAvailable
                                  && row?.Item is HardwareItem.Disk or HardwareItem.Unused or HardwareItem.MountPoint;
        _menuResize.IsEnabled = _disk.IsEnabled && row is { IsUsedDisk: true };
        _menuEnroll.IsVisible = row?.Item == HardwareItem.Efi;
        _menuEnroll.IsEnabled = _disk.IsEnabled && _ctx is not null && DiskActions.CanEnrollCertificates(_ctx);
        _add.IsEnabled = !_busy;
    }

    // ------------------------------------------------------------ 제거 · 되돌리기

    /// <summary>
    ///     제거/분리 — 디스크를 분리하면 미사용 디스크로 남고(데이터 보존), 미사용 디스크를 지우면 이미지가 영구 삭제된다
    ///     (그래서 이름을 직접 입력해야 한다). 미사용 디스크나 실행 중 VM 의 디스크는 백그라운드 작업으로 보낸다.
    /// </summary>
    private async Task OnRemoveAsync()
    {
        if (_busy || _ctx is null || Selected is not { CanRemove: true } row) return;

        // 미사용 디스크·최대 절전 상태는 지우면 되돌릴 수 없다 — 이름을 직접 입력해야 한다
        var destroys = row.Item is HardwareItem.Unused or HardwareItem.VmState;
        var text = Loc.T(row.IsUsedDisk ? "Hw_DetachConfirm" : destroys ? "Hw_DestroyConfirm" : "Hw_RemoveConfirm",
            row.Header);
        var title = Loc.T(row.IsUsedDisk ? "Hw_Detach" : "Hw_Remove");
        if (destroys)
        {
            if (await FormDialog.ShowAsync(text, [ActionHelpers.TypeToConfirmField()],
                    ActionHelpers.TypedMatches(row.Key)) is null)
                return;
        }
        else if (!await Ask.ConfirmAsync(text, title, destructive: true))
        {
            return;
        }

        // CT 는 늘 PUT, VM 은 미사용 디스크·실행 중 디스크를 백그라운드 작업으로
        var background = !_isCt && (destroys || (row.IsDisk || row.Item == HardwareItem.CloudInit) && _ctx.IsRunning);
        await SendAsync(new Dictionary<string, string> { [row.Key] = string.Empty }, background,
            Loc.T(row.IsUsedDisk ? "DeviceTable_Detached" : "Hw_Removed", "OK"), title, confirmed: !destroys);
    }

    private async Task OnRevertAsync()
    {
        if (_busy || _ctx is null || Selected is not { HasPending: true } row) return;

        var keys = row.Keys.Where(_ctx.Config.PendingKeys.Contains).ToList();
        await RunAsync(async () =>
        {
            using var flow = WriteConfirmation.Begin(Loc.T("OptionsTab_Revert"));
            await _api.RevertGuestPendingAsync(_guest.Node, _guest.Kind, _guest.VmId, keys);
            return Loc.T("OptionsTab_Reverted", row.Header);
        });
    }

    // ------------------------------------------------------------ 보내기

    /// <summary>
    ///     설정 변경을 보낸다 — 빈 값은 delete 로. background 면 웹 UI 처럼 POST …/config + background_delay=5 로 보내고
    ///     생긴 작업(UPID)이 끝날 때까지 기다린다. 쓰기 확인 흐름 이름은 <paramref name="flowName" />.
    /// </summary>
    private Task SendAsync(IReadOnlyDictionary<string, string> changes, bool background, string doneText,
        string flowName, bool confirmed = false)
    {
        var form = ActionHelpers.UpdateForm(changes);
        return RunAsync(async () =>
        {
            using var flow = WriteConfirmation.Begin(flowName, confirmed);
            if (!background)
            {
                await _api.UpdateGuestConfigAsync(_guest.Node, _guest.Kind, _guest.VmId, form);
                return doneText;
            }

            form["background_delay"] = "5";
            var status = await ActionHelpers.RunTaskAsync(_api, _api.Guests.UpdateConfigAsync(_guest, form),
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
            _status.Text = Loc.T("GuestSettingsWindow_M05", 1);
            _status.Text = await work();
        }
        catch (WriteCancelledException)
        {
            _status.Text = string.Empty; // 확인 창에서 취소 — 아무 일도 없었다
        }
        catch (Exception ex)
        {
            _status.Text = Loc.T("AppSettingsWindow_M03", ex.Message);
            HardwareLog.Warn($"[하드웨어] {_guest.VmId} 작업 실패: {ex.Message}");
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }

        await ReloadAsync();
    }

    /// <summary>
    ///     편집 창을 띄우고 확인하면 바뀐 설정만 보낸다(대기 중 값 기준으로 같은 값은 뺀다).
    /// </summary>
    private async Task ApplyEditAsync(HardwareEdit edit)
    {
        // 설정 저장 요청을 알리면 서버가 모르는 설정 칸(예: 9.0 전의 allow-ksm)은 창이 뺀다
        var values = await FormDialog.ShowAsync(edit.Title, edit.Fields, edit.Validate,
            _api.Guests.Feature(nameof(GuestsApi.SetConfigAsync)));
        if (values is null || _ctx is null) return;

        var changes = edit.Build(values)
            .Where(kv => _ctx.Effective.GetValueOrDefault(kv.Key, "") != kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (changes.Count == 0)
        {
            _status.Text = Loc.T("GuestSettingsWindow_M04");
            return;
        }

        await SendAsync(changes, edit.Background, Loc.T("GuestSettingsWindow_M06"), edit.Title);
    }

    /// <summary>디스크 작업(move_disk·resize)을 보내고 작업이 끝날 때까지 기다린다.</summary>
    private async Task ApplyDiskActionAsync(HardwareEdit edit,
        Func<IReadOnlyDictionary<string, string>, DiskActions.DiskRequest> request)
    {
        var values = await FormDialog.ShowAsync(edit.Title, edit.Fields, edit.Validate,
            _api.Guests.Feature(nameof(GuestsApi.MoveDiskAsync)));
        if (values is null) return;

        var r = request(values);
        await RunAsync(async () =>
        {
            using var flow = WriteConfirmation.Begin(edit.Title);
            return await ActionHelpers.RunTaskAsync(_api, r.Send(_api), "Hw_TaskDone");
        });
    }
}

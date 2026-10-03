using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.App.ViewModels;
using ProxmoxClient.App.Views;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.Core.Settings;

namespace ProxmoxClient.App;

public partial class MainWindow : Window
{
    private readonly AppSettingsStore _appSettingsStore = new();
    private readonly MainViewModel _vm = new();
    private AppSettings _appSettings = new();
    private bool _wizardOpen;
    private ConnectionProfile? _autoConnectProfile;

    public MainWindow(ConnectionProfile? autoConnectProfile = null)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        DataContext = _vm;
        _vm.ConfirmCertificate = (rejection, profile) => CertificateTrust.Confirm(this, rejection, profile);
        // 전원 확인은 지금 앞에 있는 창(콘솔 창에서 누른 경우 그 창) 위에 띄운다
        _vm.ConfirmPowerAction = text => ThemedMessageBox.Show(
            Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this, text,
            Loc.T("TableTab_ConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        _autoConnectProfile = autoConnectProfile;
        Loaded += OnLoadedAsync;
        Closing += (_, _) => _vm.Dispose();
        StateChanged += (_, _) => _vm.SetBackgrounded(WindowState == WindowState.Minimized);
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        _appSettings = await _appSettingsStore.LoadAsync();
        ByteFormatter.DisplayUnit = _appSettings.ByteUnit;
        _vm.ApplyStartupSettings(_appSettings);
        _vm.ShowTemplates = _appSettings.ShowTemplates;
        ApplyTemplateColumn();
        GuestDetailRow.Height = new GridLength(_appSettings.GuestDetailHeight);
        NodeListColumn.Width = new GridLength(_appSettings.NodeListWidth);
        await _vm.LoadProfilesAsync();
        if (_autoConnectProfile is { } profile)
        {
            _autoConnectProfile = null;
            _ = _vm.ConnectWithProfileAsync(profile);
        }
    }

    private void OnSwitchServer(object sender, RoutedEventArgs e)
    {
        OpenLoginDialog();
    }

    private void OnOpenAppSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new AppSettingsWindow(_appSettings) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SavedSettings is { } saved)
        {
            _appSettings = saved;
            _vm.ApplyRefreshInterval(saved.RefreshInterval); // 저장 즉시 새 간격 적용
            ByteFormatter.DisplayUnit = saved.ByteUnit; // 다음 새로고침부터 모든 용량 표시에 반영
        }
    }

    /// <summary>템플릿 표시를 바꾸면 '템플릿' 열도 함께 숨기거나 보이고, 다음 실행을 위해 저장한다.</summary>
    private async void OnShowTemplatesClick(object sender, RoutedEventArgs e)
    {
        ApplyTemplateColumn();
        _appSettings = _appSettings with { ShowTemplates = _vm.ShowTemplates };
        try
        {
            await _appSettingsStore.SaveAsync(_appSettings);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            App.Log($"[설정] 템플릿 표시 저장 실패: {ex.Message}");
        }
    }

    /// <summary>영역 크기 손잡이를 놓았다 — 다음 실행에도 같은 크기로 열리게 저장한다.</summary>
    private async void OnPaneSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _appSettings = (_appSettings with
        {
            GuestDetailHeight = GuestDetailRow.ActualHeight,
            NodeListWidth = NodeListColumn.ActualWidth
        }).Normalize();
        try
        {
            await _appSettingsStore.SaveAsync(_appSettings);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            App.Log($"[설정] 영역 크기 저장 실패: {ex.Message}");
        }
    }

    /// <summary>템플릿을 숨기면 목록에 템플릿이 없으므로 '템플릿' 열도 필요 없다.</summary>
    private void ApplyTemplateColumn()
    {
        TemplateColumn.Visibility = _vm.ShowTemplates ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenLoginDialog()
    {
        var dialog = new LoginWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ResultProfile is not null)
            _ = _vm.ConnectWithProfileAsync(dialog.ResultProfile);
    }

    private void OnOpenSnapshot(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGuest is not { } guest)
        {
            ThemedMessageBox.Show(this, Loc.T("MainWindow_M01"), Loc.T("MainWindow_M02"));
            return;
        }

        if (_vm.Api is not { } api)
        {
            ThemedMessageBox.Show(this, Loc.T("MainWindow_M03"), Loc.T("MainWindow_M04"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        OpenGuestWindow(api, guest, "snapshots");
    }

    /// <summary>만들기 버튼 — 웹 UI 처럼 "VM 만들기" / "CT 만들기" 마법사를 고른다.</summary>
    private void OnCreateGuest(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(CreateMenuItem("Wz_CreateVm", Views.Create.VmWizard.ShowAsync));
        menu.Items.Add(CreateMenuItem("Wz_CreateCt", Views.Create.CtWizard.ShowAsync));
        menu.IsOpen = true;
    }

    private MenuItem CreateMenuItem(string key,
        Func<Window, ProxmoxClient.Core.Api.ProxmoxApiClient, IReadOnlyList<string>, Task<string?>> show)
    {
        var item = new MenuItem { Header = Loc.T(key) };
        item.Click += async (_, _) =>
        {
            if (_vm.Api is not { } api || _wizardOpen) return;
            // 선택한 게스트의 노드를 먼저 — 웹 UI 도 선택한 노드에서 마법사를 연다
            var selected = _vm.SelectedGuest?.Node;
            var nodes = _vm.Nodes.Where(n => n.IsOnline).Select(n => n.Node)
                .OrderBy(n => n == selected ? 0 : 1).ToList();
            if (nodes.Count == 0)
            {
                ThemedMessageBox.Show(this, Loc.T("Wz_NoOnlineNode"), Loc.T(key), MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _wizardOpen = true;
            _vm.StatusMessage = Loc.T("Hw_Loading");
            try
            {
                _vm.StatusMessage = await show(this, api, nodes) ?? string.Empty;
            }
            catch (Exception ex)
            {
                App.Log($"[만들기] 마법사 열기 실패: {ex.Message}");
                _vm.StatusMessage = string.Empty;
                ThemedMessageBox.Show(this, Loc.T("Wz_LoadFailed", ex.Message), Loc.T(key),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _wizardOpen = false;
            }

            _ = _vm.RefreshDataAsync();
        };
        return item;
    }

    private void OnMenuFirewall(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGuest is not { } guest || _vm.Api is not { } api) return;

        OpenGuestWindow(api, guest, "firewall");
    }

    private void OnMenuSettings(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGuest is not { } guest || _vm.Api is not { } api) return;

        OpenGuestWindow(api, guest, "hardware");
    }

    /// <summary>게스트 통합 창을 연다 — 웹 UI 처럼 한 창에서 탭으로 오간다.</summary>
    private void OpenGuestWindow(ProxmoxClient.Core.Api.ProxmoxApiClient api, PveResource guest, string tabId)
    {
        var permissions = _vm.Permissions ?? PermissionsInfo.Admin;
        var window = Views.Guest.GuestNavigator.Create(api, guest, permissions, _vm.PowerRunnerFor(api), tabId);
        window.Owner = this;
        window.ShowDialog();
        _ = _vm.RefreshDataAsync();
    }

    private void OnOpenDatacenter(object sender, RoutedEventArgs e)
    {
        if (_vm.Api is not { } api)
        {
            ThemedMessageBox.Show(this, Loc.T("MainWindow_M03"), Loc.T("MainWindow_M04"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var window = Views.Datacenter.DatacenterNavigator.Create(api, _vm.Permissions ?? PermissionsInfo.Admin,
            openResource: (row, owner) => OpenResourceWindow(api, row, owner));
        window.Owner = this;
        window.ShowDialog();
        _ = _vm.RefreshDataAsync();
    }

    /// <summary>
    ///     데이터센터 검색 탭의 행(cluster/resources 한 줄)에 맞는 창을 연다 — 노드·게스트·저장소.
    ///     게스트·저장소는 메인 목록의 같은 객체를 써서 전원 동작·상태 표시가 메인 창과 이어지게 한다.
    /// </summary>
    private void OpenResourceWindow(ProxmoxClient.Core.Api.ProxmoxApiClient api,
        IReadOnlyDictionary<string, string> row, Window? owner)
    {
        string Value(string key) => row.TryGetValue(key, out var v) ? v : string.Empty;
        var permissions = _vm.Permissions ?? PermissionsInfo.Admin;
        var node = Value("node");

        if (Value("type") == "pool")
        {
            // 풀은 웹 UI 처럼 구성원 목록을 띄운다(편집 버튼은 Pool.Allocate 가 있을 때만)
            var pool = Value("pool");
            Views.Shared.TableWindow.ShowModal(owner ?? this, Loc.T("DcPools_MembersTitle", pool),
                Views.Datacenter.PoolMembers.Create(api, pool, permissions.Has("Pool.Allocate")));
            _ = _vm.RefreshDataAsync();
            return;
        }

        Window? window = Value("type") switch
        {
            "node" => Views.Node.NodeNavigator.Create(api, node, permissions),
            "qemu" or "lxc" when int.TryParse(Value("vmid"), out var vmid)
                                 && _vm.Guests.FirstOrDefault(g => g.VmId == vmid) is { } guest =>
                Views.Guest.GuestNavigator.Create(api, guest, permissions, _vm.PowerRunnerFor(api), null),
            "storage" => Views.Storage.StorageNavigator.Create(api, node, Value("storage"),
                _vm.Storages.FirstOrDefault(s => s.Node == node && s.Storage == Value("storage"))?.Content
                ?? Value("content"), permissions, Value("plugintype") is { Length: > 0 } type ? type : null),
            _ => null // SDN 은 별도 창이 없다
        };
        if (window is null) return;

        window.Owner = owner ?? this;
        window.ShowDialog();
        _ = _vm.RefreshDataAsync();
    }

    /// <summary>저장소 목록 더블클릭 — 그 저장소의 콘텐츠 창(백업·ISO·템플릿·디스크)을 연다.</summary>
    private void OnStorageDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not PveStorage storage
            || _vm.Api is not { } api)
            return;

        var window = Views.Storage.StorageNavigator.Create(api, storage.Node, storage.Storage, storage.Content,
            _vm.Permissions ?? PermissionsInfo.Admin, storage.PluginType is { Length: > 0 } type ? type : null);
        window.Owner = this;
        window.ShowDialog();
        _ = _vm.RefreshDataAsync();
    }

    private void OnNodeListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PveNode) OpenNodeWindow();
    }

    /// <summary>꺼진 노드를 Wake-on-LAN 으로 깨운다 — 요청은 연결된(켜진) 노드가 받아 패킷을 보낸다.</summary>
    private async void OnWakeNode(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedNode is not { IsOnline: false } node || _vm.Api is not { } api) return;

        var button = sender as Button;
        if (button is not null) button.IsEnabled = false; // 연타로 요청이 여러 번 가지 않게
        try
        {
            await api.Nodes.WakeOnLanAsync(node.Node);
            _vm.StatusMessage = Loc.T("NodeWake_Sent", node.Node);
        }
        catch (Exception ex)
        {
            App.Log($"[노드] {node.Node} Wake-on-LAN 실패: {ex.Message}");
            _vm.StatusMessage = Loc.T("NodeWake_Failed", node.Node, ex.Message);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private void OnOpenNodeWindow(object sender, RoutedEventArgs e)
    {
        OpenNodeWindow();
    }

    /// <summary>선택한 노드의 관리 창을 연다.</summary>
    private void OpenNodeWindow()
    {
        if (_vm.SelectedNode is not { } node || _vm.Api is not { } api) return;

        var permissions = _vm.Permissions ?? PermissionsInfo.Admin;
        var window = Views.Node.NodeNavigator.Create(api, node.Node, permissions);
        window.Owner = this;
        window.ShowDialog();
        _ = _vm.RefreshDataAsync();
    }

    private void OnGuestGridRightClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PveResource resource)
            GuestGrid.SelectedItem = resource;
    }

    /// <summary>게스트 목록 행 더블클릭 → 콘솔 열기(헤더·빈 영역 더블클릭은 무시).</summary>
    private void OnGuestGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not DataGridRow) source = VisualTreeHelper.GetParent(source);

        if (source is DataGridRow { Item: PveResource guest })
        {
            GuestGrid.SelectedItem = guest;
            e.Handled = true;
            OnOpenConsole(sender, e);
        }
    }

    /// <summary>작업 목록 행 더블클릭 → 그 작업의 로그 창.</summary>
    private void OnTaskGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Views.Shared.TaskLogViewer.RowTask(e) is not { } task || _vm.Api is not { } api) return;

        e.Handled = true;
        Views.Shared.TaskLogViewer.Show(api, task, this);
    }

    private void OnMenuConsole(object sender, RoutedEventArgs e)
    {
        OnOpenConsole(sender, e);
    }

    private void OnMenuSnapshot(object sender, RoutedEventArgs e)
    {
        OnOpenSnapshot(sender, e);
    }

    private void OnMenuClone(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGuest is not { } guest || _vm.Api is not { } api) return;

        var nodeNames = _vm.Nodes.Select(n => n.Node).ToList();
        var diskContent = guest.Kind == ResourceKind.Qemu ? "images" : "rootdir";
        var storageNames = _vm.Storages
            .Where(s => s.Content.Contains(diskContent, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Storage)
            .Distinct()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dialog = new CloneWindow(api, guest, nodeNames, storageNames) { Owner = this };
        dialog.ShowDialog();
        _ = _vm.RefreshDataAsync();
    }

    private void OnMenuBackup(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGuest is not { } guest || _vm.Api is not { } api) return;

        OpenGuestWindow(api, guest, "backup");
    }

    private async void OnOpenConsole(object sender, RoutedEventArgs e)
    {
        await OpenConsoleAsync(null);
    }

    /// <summary>게스트 콘솔을 연다 — protocol 이 없으면 기본(VM 디스플레이가 RDP 면 RDP, 아니면 VNC).</summary>
    private async Task OpenConsoleAsync(Core.Vnc.ConsoleProtocol? protocol)
    {
        if (_vm.SelectedGuest is not { } guest)
        {
            ThemedMessageBox.Show(this, Loc.T("MainWindow_M01"), Loc.T("MainWindow_M02"));
            return;
        }

        if (_vm.Api is not { } api)
        {
            ThemedMessageBox.Show(this, Loc.T("MainWindow_M03"), Loc.T("MainWindow_M04"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 정지된 게스트도 콘솔 창을 연다 — 창에서 시작하면 자동 연결(noVNC 와 동일한 흐름). 템플릿만 제외.
        if (guest.IsTemplate)
        {
            ThemedMessageBox.Show(this, Loc.T("MainWindow_M07"), Loc.T("MainWindow_M08"));
            return;
        }

        var title = $"{guest.Kind.Label()} {guest.VmId} - {guest.Name}";
        // Owner 미지정: 부모창과 독립된 최상위 창(작업 표시줄 개별 표시, 부모 최소화에 영향받지 않음)
        // 같은 게스트 콘솔이 이미 열려 있으면 새로 열지 않고 그 창을 앞으로
        var canPowerManage = _vm.Permissions?.CanPowerMgmt ?? true;
        try
        {
            if (protocol is { } chosen)
                Services.ConsoleWindows.ShowGuest(api, guest, title, _vm.PowerRunnerFor(api), canPowerManage, chosen);
            else
                await Services.ConsoleWindows.ShowPreferredAsync(api, guest, title, _vm.PowerRunnerFor(api),
                    canPowerManage);
        }
        catch (Exception ex)
        {
            // 창을 만들지 못했다(비동기 메뉴·버튼에서 불리므로 여기서 알린다)
            App.Log($"[콘솔] {guest.VmId} 열기 실패: {ex}");
            _vm.StatusMessage = Loc.T("ConsoleWindow_M07", ex.Message);
        }
    }

    private void OnGuestTfChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GuestTfBox.SelectedItem is ListBoxItem { Tag: string tf }) _vm.GuestTimeframe = tf;
    }

    private void OnNodeTfChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NodeTfBox.SelectedItem is ListBoxItem { Tag: string tf }) _vm.NodeTimeframe = tf;
    }
}
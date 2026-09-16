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
    private ConnectionProfile? _autoConnectProfile;

    public MainWindow(ConnectionProfile? autoConnectProfile = null)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        DataContext = _vm;
        _vm.ConfirmCertificate = (rejection, profile) => CertificateTrust.Confirm(this, rejection, profile);
        _autoConnectProfile = autoConnectProfile;
        Loaded += OnLoadedAsync;
        Closing += (_, _) => _vm.Dispose();
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        _appSettings = await _appSettingsStore.LoadAsync();
        ByteFormatter.DisplayUnit = _appSettings.ByteUnit;
        _vm.ApplyStartupSettings(_appSettings);
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

        var title = $"{guest.Kind.Label()} {guest.VmId} — {guest.Name}";
        var dialog = new SnapshotDialog(api, guest.Node, guest.Kind, guest.VmId, title) { Owner = this };
        dialog.ShowDialog();
        _ = _vm.RefreshDataAsync();
    }

    private void OnCreateGuest(object sender, RoutedEventArgs e)
    {
        if (_vm.Api is not { } api) return;

        var dialog = new CreateGuestWindow(api, _vm.Nodes.ToList(), _vm.Storages.ToList()) { Owner = this };
        dialog.ShowDialog();
        _ = _vm.RefreshDataAsync();
    }

    private void OnMenuFirewall(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGuest is not { } guest || _vm.Api is not { } api) return;

        new FirewallWindow(api, guest) { Owner = this }.ShowDialog();
    }

    private void OnMenuSettings(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGuest is not { } guest || _vm.Api is not { } api) return;

        var dialog = new GuestSettingsWindow(api, guest) { Owner = this };
        dialog.ShowDialog();
        _ = _vm.RefreshDataAsync();
    }

    private void OnGuestGridRightClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PveResource resource)
            GuestGrid.SelectedItem = resource;
    }

    /// <summary>상세 패널의 "⋯" 버튼 — 버튼에 붙은 ContextMenu 를 버튼 아래에 연다.</summary>
    private void OnMoreActions(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
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

        var backupStorages = _vm.Storages.Where(s => s.Content.Contains("backup", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (backupStorages.Count == 0)
        {
            ThemedMessageBox.Show(this,
                Loc.T("MainWindow_M05"),
                Loc.T("MainWindow_M06"));
            return;
        }

        var dialog = new BackupWindow(api, guest, backupStorages) { Owner = this };
        dialog.ShowDialog();
        _ = _vm.RefreshDataAsync();
    }

    private void OnOpenConsole(object sender, RoutedEventArgs e)
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

        var title = $"{guest.Kind.Label()} {guest.VmId} — {guest.Name}";
        // Owner 미지정: 부모창과 독립된 최상위 창(작업 표시줄 개별 표시, 부모 최소화에 영향받지 않음)
        // VM 은 VNC 그래픽 콘솔, CT 는 termproxy 터미널
        var canPowerManage = _vm.Permissions?.CanPowerMgmt ?? true;
        Window console = guest.Kind == ResourceKind.Lxc
            ? new TerminalWindow(api, guest, title, _vm.RunGuestPowerForAsync, canPowerManage)
            : new ConsoleWindow(api, guest, title, _vm.RunGuestPowerForAsync, canPowerManage);
        console.Show();
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
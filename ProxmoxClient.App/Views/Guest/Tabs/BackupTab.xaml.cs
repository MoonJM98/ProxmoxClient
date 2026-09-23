using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>게스트 창의 백업 탭 — 저장소·모드·압축을 고르고 백업 작업을 시작한다.</summary>
public partial class BackupTab : UserControl
{
    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private bool _busy;

    public BackupTab(ProxmoxApiClient api, PveResource guest)
    {
        InitializeComponent();
        _api = api;
        _guest = guest;
        Loaded += async (_, _) => await LoadStoragesAsync();
    }

    /// <summary>backup 콘텐츠를 지원하는 저장소만 고른다 — 나머지는 백업 대상이 될 수 없다.</summary>
    private async Task LoadStoragesAsync()
    {
        try
        {
            var storages = await _api.GetClusterStoragesAsync();
            var backupStorages = storages
                .Where(s => s.Content.Contains("backup", StringComparison.OrdinalIgnoreCase))
                .ToList();

            StorageBox.ItemsSource = backupStorages;
            if (backupStorages.Count > 0)
            {
                StorageBox.SelectedIndex = 0;
            }
            else
            {
                SetStatus(Loc.T("BackupWindow_M01"));
                BtnBackup.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("BackupWindow_M04", ex.Message));
        }
    }

    private async void OnBackup(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (StorageBox.SelectedItem is not PveStorage storage)
        {
            SetStatus(Loc.T("BackupWindow_M01"));
            return;
        }

        var mode = RadioSnapshot.IsChecked == true ? "snapshot"
            : RadioSuspend.IsChecked == true ? "suspend"
            : "stop";
        var compress = RadioZstd.IsChecked == true ? "zstd"
            : RadioLzo.IsChecked == true ? "lzo"
            : RadioGzip.IsChecked == true ? "gzip"
            : string.Empty;

        _busy = true;
        BtnBackup.IsEnabled = false;
        SetStatus(Loc.T("BackupWindow_M02"));

        try
        {
            await _api.BackupGuestAsync(_guest.Node, _guest.Kind, _guest.VmId, storage.Storage, mode, compress);
            SetStatus(Loc.T("BackupWindow_M03"));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("BackupWindow_M04", ex.Message));
        }
        finally
        {
            _busy = false;
            BtnBackup.IsEnabled = true;
        }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }
}

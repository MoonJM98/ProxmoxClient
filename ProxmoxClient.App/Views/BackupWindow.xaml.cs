using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views;

public partial class BackupWindow : Window
{
    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private bool _busy;

    public BackupWindow(ProxmoxApiClient api, PveResource guest, IReadOnlyList<PveStorage> backupStorages)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _api = api;
        _guest = guest;
        HeaderText.Text = Loc.T("BackupWindow_Header", guest.Kind.Label(), guest.VmId, guest.Name);
        StorageBox.ItemsSource = backupStorages;
        if (backupStorages.Count > 0) StorageBox.SelectedIndex = 0;
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
            var upid = await _api.BackupGuestAsync(
                _guest.Node, _guest.Kind, _guest.VmId, storage.Storage, mode, compress);
            SetStatus(Loc.T("BackupWindow_M03"));
            await Task.Delay(900);
            DialogResult = true;
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
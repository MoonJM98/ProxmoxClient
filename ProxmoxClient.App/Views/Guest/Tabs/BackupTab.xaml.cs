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
    private bool _loaded;

    /// <summary>사용자가 모드·압축을 직접 골랐는지 — 그 뒤 도착한 기본값으로 덮지 않는다.</summary>
    private bool _userPicked;

    public BackupTab(ProxmoxApiClient api, PveResource guest)
    {
        InitializeComponent();
        _api = api;
        _guest = guest;
        foreach (var radio in new[]
                 {
                     RadioSnapshot, RadioSuspend, RadioStop, RadioNoCompress, RadioZstd, RadioLzo, RadioGzip
                 })
            radio.PreviewMouseDown += (_, _) => _userPicked = true;
        StorageBox.DropDownOpened += (_, _) => _userPicked = true;
        Loaded += async (_, _) =>
        {
            if (_loaded) return; // 탭을 다시 보일 때마다 저장소·선택을 되돌리지 않는다
            _loaded = true;
            await LoadStoragesAsync();
        };
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
                await ApplyDefaultsAsync(backupStorages);
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

    /// <summary>
    ///     노드 백업 기본값(/etc/vzdump.conf)의 저장소·모드·압축을 미리 골라 둔다 — 못 읽으면 지금 선택 그대로.
    /// </summary>
    private async Task ApplyDefaultsAsync(IReadOnlyList<PveStorage> storages)
    {
        try
        {
            var defaults = await _api.Nodes.VzdumpDefaultsAsync(_guest.Node);
            if (_userPicked) return;
            if (defaults.TryGetValue("storage", out var storage)
                && storages.FirstOrDefault(s => s.Storage == storage) is { } match)
                StorageBox.SelectedItem = match;
            if (defaults.TryGetValue("mode", out var mode))
                (mode switch { "suspend" => RadioSuspend, "stop" => RadioStop, _ => RadioSnapshot }).IsChecked = true;
            if (defaults.TryGetValue("compress", out var compress))
                (compress switch
                {
                    "zstd" => RadioZstd, "lzo" or "1" => RadioLzo, "gzip" => RadioGzip, _ => RadioNoCompress
                }).IsChecked = true;
        }
        catch (Exception ex) when (ex is ProxmoxApiException or System.Net.Http.HttpRequestException
                                       or TaskCanceledException)
        {
            App.Log($"[백업] {_guest.Node} 백업 기본값 읽기 실패: {ex.Message}");
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

using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Versioning;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>
///     게스트 창의 백업 탭 — 웹 UI 백업 창처럼 저장소·모드·압축·알림·보호·메모를 고르고 백업 작업을 시작한다.
///     알림(8.1)·보호·메모(7.1)는 서버가 모르는 버전이면 칸을 숨기고 보내지 않는다.
/// </summary>
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
        ComboChoices.Fill(ModeBox,
            [("snapshot", "BackupWindow_06"), ("suspend", "BackupWindow_07"), ("stop", "BackupWindow_08")]);
        ComboChoices.Fill(CompressBox,
        [
            ("", "BackupWindow_10"), ("zstd", "BackupTab_CompressZstd"), ("lzo", "BackupTab_CompressLzo"),
            ("gzip", "BackupTab_CompressGzip")
        ]);
        ComboChoices.Fill(NotifyBox,
        [
            ("", "DcBackup_NotifyAuto"), ("notification-system", "DcBackup_NotifySystem"),
            ("legacy-sendmail", "DcBackup_NotifyEmail")
        ]);
        ModeBox.SelectedIndex = 0;
        ComboChoices.Select(CompressBox, "zstd"); // 웹 UI 기본값
        NotifyBox.SelectedIndex = 0;

        foreach (var box in new[] { StorageBox, ModeBox, CompressBox })
            box.DropDownOpened += (_, _) => _userPicked = true;
        Loaded += async (_, _) =>
        {
            if (_loaded) return; // 탭을 다시 보일 때마다 저장소·선택을 되돌리지 않는다
            _loaded = true;
            await ShowSupportedOptionsAsync();
            await LoadStoragesAsync();
        };
    }

    /// <summary>서버가 모르는 칸은 숨긴다 — 버전을 못 읽으면 모두 보인다(서버가 거절하면 오류로 알린다).</summary>
    private async Task ShowSupportedOptionsAsync()
    {
        try
        {
            await _api.GetServerVersionAsync();
        }
        catch (Exception ex) when (ex is ProxmoxApiException or System.Net.Http.HttpRequestException
                                       or TaskCanceledException)
        {
            App.Log($"[백업] 서버 버전 읽기 실패: {ex.Message}");
        }

        var notesAndProtect = _api.Supports(PveApiVersion.Parse("7.1"));
        RowNotes.Visibility = RowProtected.Visibility = notesAndProtect ? Visibility.Visible : Visibility.Collapsed;
        RowNotify.Visibility = _api.Supports(PveApiVersion.Parse("8.1")) ? Visibility.Visible : Visibility.Collapsed;
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
            if (defaults.TryGetValue("mode", out var mode)) ComboChoices.Select(ModeBox, mode);
            // 서버는 설정하지 않은 압축도 스키마 기본값 "0"(없음)으로 돌려주므로, 그때는 웹 UI 처럼 ZSTD 를 둔다
            if (defaults.TryGetValue("compress", out var compress) && compress is not ("0" or ""))
                ComboChoices.Select(CompressBox, compress == "1" ? "lzo" : compress);
            if (defaults.TryGetValue("notification-mode", out var notify))
                ComboChoices.Select(NotifyBox, notify == "auto" ? "" : notify);
            if (defaults.TryGetValue("protected", out var isProtected))
                ProtectedCheck.IsChecked = isProtected is "1" or "true";
            if (defaults.TryGetValue("notes-template", out var notes) && notes.Length > 0)
                NotesBox.Text = notes.Replace("\\n", "\n", StringComparison.Ordinal)
                    .Replace("\\\\", "\\", StringComparison.Ordinal);
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

        var mode = ComboChoices.Selected(ModeBox);
        var compress = ComboChoices.Selected(CompressBox);

        _busy = true;
        BtnBackup.IsEnabled = false;
        SetStatus(Loc.T("BackupWindow_M02"));

        try
        {
            await _api.BackupGuestAsync(_guest.Node, _guest.Kind, _guest.VmId, storage.Storage, mode, compress,
                ExtraOptions());
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

    /// <summary>보이는 추가 칸만 보낸다 — 숨긴 칸(서버가 모르는 버전)은 빈 값이라 보내지 않는다.</summary>
    private Dictionary<string, string> ExtraOptions()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (RowNotify.Visibility == Visibility.Visible) options["notification-mode"] = ComboChoices.Selected(NotifyBox);
        if (RowProtected.Visibility == Visibility.Visible && ProtectedCheck.IsChecked == true)
            options["protected"] = "1";
        // 서버는 메모 템플릿을 한 줄로 받는다 — 역슬래시·줄바꿈은 두 글자 표기(역슬래시 두 개, 역슬래시+n)로 보낸다
        if (RowNotes.Visibility == Visibility.Visible)
            options["notes-template"] = NotesBox.Text.Trim()
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
        return options;
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }
}

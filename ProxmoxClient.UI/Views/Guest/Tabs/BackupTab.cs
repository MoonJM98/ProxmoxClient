using System.Diagnostics;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Versioning;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Guest.Tabs;

/// <summary>
///     게스트 백업 탭 — 웹 UI 백업 창처럼 저장소·모드·압축·알림·보호·메모를 고르고 백업 작업을 시작한다.
///     알림(8.1)·보호·메모(7.1)는 서버가 모르는 버전이면 칸을 숨기고 보내지 않는다.
/// </summary>
public sealed class BackupTab : UserControl
{
    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private readonly ComboBox _storage = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _mode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _compress = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _notify = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _protected = new();
    private readonly TextBox _notes = new()
    {
        Text = "{{guestname}}", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64,
        VerticalContentAlignment = VerticalAlignment.Top
    };
    private readonly StackPanel _rowNotify;
    private readonly StackPanel _rowProtected;
    private readonly StackPanel _rowNotes;
    private readonly Button _start;
    private readonly TextBlock _status = new() { Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap };
    private List<PveStorage> _storages = [];
    private bool _busy;
    private bool _loaded;

    /// <summary>사용자가 저장소·모드·압축을 직접 골랐는지 — 그 뒤 도착한 기본값으로 덮지 않는다.</summary>
    private bool _userPicked;

    public BackupTab(ProxmoxApiClient api, PveResource guest)
    {
        _api = api;
        _guest = guest;

        ComboChoices.Fill(_mode,
            [("snapshot", "BackupWindow_06"), ("suspend", "BackupWindow_07"), ("stop", "BackupWindow_08")]);
        ComboChoices.Fill(_compress,
        [
            ("", "BackupWindow_10"), ("zstd", "BackupTab_CompressZstd"), ("lzo", "BackupTab_CompressLzo"),
            ("gzip", "BackupTab_CompressGzip")
        ]);
        ComboChoices.Fill(_notify,
        [
            ("", "DcBackup_NotifyAuto"), ("notification-system", "DcBackup_NotifySystem"),
            ("legacy-sendmail", "DcBackup_NotifyEmail")
        ]);
        _mode.SelectedIndex = 0;
        ComboChoices.Select(_compress, "zstd"); // 웹 UI 기본값
        _notify.SelectedIndex = 0;

        foreach (var box in new[] { _storage, _mode, _compress })
            box.DropDownOpened += (_, _) => _userPicked = true;

        _rowNotify = GuestFormRow.Create("DcBackup_NotifyMode", _notify);
        _rowProtected = GuestFormRow.Create("DcBackup_Protected", _protected, "BackupTab_ProtectedHint");
        _rowNotes = GuestFormRow.Create("DcBackup_NotesTemplate", _notes, "BackupTab_NotesHint");

        _start = ActionBar.IconButton(Loc.T("BackupWindow_12"), "IconArchive", this);
        _start.Classes.Add("accent");
        _start.Click += async (_, _) => await BackupAsync();

        var form = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 12,
            Children =
            {
                GuestFormRow.Create("BackupWindow_03", _storage, "BackupWindow_04"),
                GuestFormRow.Create("BackupWindow_05", _mode),
                GuestFormRow.Create("BackupWindow_09", _compress),
                _rowNotify,
                _rowProtected,
                _rowNotes
            }
        };
        var bottom = new StackPanel
        {
            Margin = new Thickness(16, 0, 16, 16), Spacing = 8,
            Children =
            {
                new TextBlock { Text = Loc.T("BackupWindow_11"), Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap },
                _start,
                _status
            }
        };
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var scroll = new ScrollViewer { Content = form };
        Grid.SetRow(scroll, 0);
        Grid.SetRow(bottom, 1);
        grid.Children.Add(scroll);
        grid.Children.Add(bottom);
        Content = grid;

        AttachedToVisualTree += async (_, _) =>
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
        catch (Exception ex) when (ex is ProxmoxApiException or HttpRequestException or TaskCanceledException)
        {
            Debug.WriteLine($"[백업] 서버 버전 읽기 실패: {ex.Message}");
        }

        var notesAndProtect = _api.Supports(PveApiVersion.Parse("7.1"));
        _rowNotes.IsVisible = _rowProtected.IsVisible = notesAndProtect;
        _rowNotify.IsVisible = _api.Supports(PveApiVersion.Parse("8.1"));
    }

    /// <summary>backup 콘텐츠를 지원하는 저장소만 고른다 — 나머지는 백업 대상이 될 수 없다.</summary>
    private async Task LoadStoragesAsync()
    {
        try
        {
            var storages = await _api.GetClusterStoragesAsync();
            _storages = storages.Where(s => s.Content.Contains("backup", StringComparison.OrdinalIgnoreCase)).ToList();
            _storage.ItemsSource = _storages.Select(s => s.Storage).ToList();
            if (_storages.Count > 0)
            {
                _storage.SelectedIndex = 0;
                await ApplyDefaultsAsync();
            }
            else
            {
                SetStatus(Loc.T("BackupWindow_M01"));
                _start.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("BackupWindow_M04", ex.Message));
        }
    }

    /// <summary>노드 백업 기본값(/etc/vzdump.conf)의 저장소·모드·압축을 미리 골라 둔다 — 못 읽으면 지금 선택 그대로.</summary>
    private async Task ApplyDefaultsAsync()
    {
        try
        {
            var defaults = await _api.Nodes.VzdumpDefaultsAsync(_guest.Node);
            if (_userPicked) return;

            if (defaults.TryGetValue("storage", out var storage))
            {
                var index = _storages.FindIndex(s => s.Storage == storage);
                if (index >= 0) _storage.SelectedIndex = index;
            }

            if (defaults.TryGetValue("mode", out var mode)) ComboChoices.Select(_mode, mode);
            // 서버는 설정하지 않은 압축도 스키마 기본값 "0"(없음)으로 돌려주므로, 그때는 웹 UI 처럼 ZSTD 를 둔다
            if (defaults.TryGetValue("compress", out var compress) && compress is not ("0" or ""))
                ComboChoices.Select(_compress, compress == "1" ? "lzo" : compress);
            if (defaults.TryGetValue("notification-mode", out var notify))
                ComboChoices.Select(_notify, notify == "auto" ? string.Empty : notify);
            if (defaults.TryGetValue("protected", out var isProtected))
                _protected.IsChecked = isProtected is "1" or "true";
            if (defaults.TryGetValue("notes-template", out var notes) && notes.Length > 0)
                _notes.Text = notes.Replace("\\n", "\n", StringComparison.Ordinal)
                    .Replace("\\\\", "\\", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ProxmoxApiException or HttpRequestException or TaskCanceledException)
        {
            Debug.WriteLine($"[백업] {_guest.Node} 백업 기본값 읽기 실패: {ex.Message}");
        }
    }

    private async Task BackupAsync()
    {
        if (_busy) return;

        var index = _storage.SelectedIndex;
        if (index < 0 || index >= _storages.Count)
        {
            SetStatus(Loc.T("BackupWindow_M01"));
            return;
        }

        var storage = _storages[index];
        var mode = ComboChoices.Selected(_mode);
        var compress = ComboChoices.Selected(_compress);

        _busy = true;
        _start.IsEnabled = false;
        SetStatus(Loc.T("BackupWindow_M02"));
        try
        {
            using (WriteConfirmation.Begin(Loc.T("BackupWindow_12")))
                await _api.BackupGuestAsync(_guest.Node, _guest.Kind, _guest.VmId, storage.Storage, mode, compress,
                    ExtraOptions());
            SetStatus(Loc.T("BackupWindow_M03"));
        }
        catch (WriteCancelledException)
        {
            SetStatus(string.Empty);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("BackupWindow_M04", ex.Message));
        }
        finally
        {
            _busy = false;
            _start.IsEnabled = true;
        }
    }

    /// <summary>보이는 추가 칸만 보낸다 — 숨긴 칸(서버가 모르는 버전)은 보내지 않는다.</summary>
    private Dictionary<string, string> ExtraOptions()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_rowNotify.IsVisible) options["notification-mode"] = ComboChoices.Selected(_notify);
        if (_rowProtected.IsVisible && _protected.IsChecked == true) options["protected"] = "1";
        // 서버는 메모 템플릿을 한 줄로 받는다 — 역슬래시·줄바꿈은 두 글자 표기(역슬래시 두 개, 역슬래시+n)로 보낸다
        if (_rowNotes.IsVisible)
            options["notes-template"] = (_notes.Text ?? string.Empty).Trim()
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
        return options;
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
    }
}

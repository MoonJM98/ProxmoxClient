using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Guest.Hardware;

namespace ProxmoxClient.UI.Views.Guest;

/// <summary>
///     게스트 복제 화면 — WPF CloneWindow 와 같은 칸(새 ID · 이름 · 모드 · 대상 노드 · 저장소)과 같은 아래쪽 단추.
///     연결 복제는 템플릿에서만 되므로 템플릿이 아니면 모드 줄을 숨기고 전체 복제만 한다.
///     서버 쓰기는 쓰기 확인 흐름(<see cref="WriteConfirmation" />)을 거쳐 작업 이름이 확인 창에 보인다.
/// </summary>
public sealed class CloneWindow : UserControl
{
    private const int MinVmid = 100;

    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    // WPF: 새 VMID 칸은 140 폭, 콤보는 줄 폭을 채운다. 휴대폰은 칸을 화면 폭을 꽉 채운다
    private readonly TextBox _newId = HardwareInput.Numeric(decimals: false, width: PlatformInfo.IsDesktop ? 140 : 0);
    private readonly TextBox _name = new();
    private readonly RadioButton _full;
    private readonly RadioButton _linked;
    private readonly ComboBox _targetNode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _storage = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Control _rowMode;
    private readonly Button _clone;
    private readonly TextBlock _status = new() { Classes = { "DialogStatus" }, TextWrapping = TextWrapping.Wrap };
    private bool _busy;

    public CloneWindow(ProxmoxApiClient api, PveResource guest)
    {
        _api = api;
        _guest = guest;
        _newId.Text = (guest.VmId + 1).ToString();

        var group = "clone-mode-" + Guid.NewGuid().ToString("N");
        _full = new RadioButton { Content = Loc.T("CloneWindow_06"), GroupName = group, IsChecked = true };
        _linked = new RadioButton { Content = Loc.T("CloneWindow_07"), GroupName = group };
        _full.IsCheckedChanged += (_, _) => _storage.IsEnabled = _full.IsChecked == true;
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _full, _linked } };

        // 웹 UI 처럼 템플릿은 연결 복제가 기본, 템플릿이 아니면 전체 복제만 가능하므로 모드를 묻지 않는다
        if (guest.IsTemplate) _linked.IsChecked = true;

        _rowMode = HardwareInput.Form(Loc.T("BackupWindow_05"), modes, Loc.T("CloneWindow_05"));
        _rowMode.IsVisible = guest.IsTemplate;

        _clone = HardwareInput.Button(Loc.T("CloneWindow_11"), "IconCopy");
        _clone.Classes.Add("DialogPrimaryButton");
        _clone.Click += async (_, _) => await CloneAsync();
        var cancel = HardwareInput.Button(Loc.T("AppSettingsWindow_13"), "IconX");
        cancel.Classes.Add("DialogButton");
        cancel.Click += async (_, _) => await AppServices.Navigator.PopAsync();

        var header = new TextBlock
        {
            Text = $"{guest.Kind.Label()} {guest.VmId} - {guest.Name}", FontSize = 15,
            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
        };
        var form = new StackPanel
        {
            Margin = new Thickness(20, 16, 20, 10), Spacing = 12,
            Children =
            {
                header,
                HardwareInput.Form(Loc.T("CloneWindow_03"), _newId),
                HardwareInput.Form(Loc.T("MainWindow_40"), _name, Loc.T("CloneWindow_04")),
                _rowMode,
                HardwareInput.Form(Loc.T("CloneWindow_08"), _targetNode),
                HardwareInput.Form(Loc.T("CloneWindow_09"), _storage, Loc.T("CloneWindow_10"))
            }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _clone, cancel } };
        DockPanel.SetDock(buttons, Dock.Right);
        var footer = new Border
        {
            Classes = { "DialogFooter" },
            Child = new DockPanel { Children = { buttons, _status } }
        };

        Content = new DockPanel
        {
            Children =
            {
                footer,
                new ScrollViewer { Content = form, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }
            }
        };
        DockPanel.SetDock(footer, Dock.Bottom);

        // 전체 복제가 아니면 저장소를 고를 수 없다(연결 복제는 원본 디스크를 공유한다)
        _storage.IsEnabled = _full.IsChecked == true;
        AttachedToVisualTree += async (_, _) => await LoadChoicesOnceAsync();
    }

    private bool _loaded;

    /// <summary>대상 노드·저장소 목록과 다음 빈 VMID — WPF 는 창을 열 때 받아 온 목록을 넘겼다.</summary>
    private async Task LoadChoicesOnceAsync()
    {
        if (_loaded) return;

        _loaded = true;
        try
        {
            var nodes = (await _api.GetNodesAsync()).Select(n => n.Node).Where(n => n != _guest.Node).ToList();
            var targets = new List<Choice> { new("", Loc.T("CloneWindow_M01", _guest.Node)) };
            targets.AddRange(nodes.Select(n => new Choice(n, n)));
            _targetNode.ItemsSource = targets;
            _targetNode.SelectedIndex = 0;

            var diskContent = _guest.Kind == ResourceKind.Qemu ? "images" : "rootdir";
            var storages = new List<Choice> { new("", Loc.T("CloneWindow_M02")) };
            storages.AddRange((await _api.GetClusterStoragesAsync())
                .Where(s => s.Content.Contains(diskContent, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Storage)
                .Distinct()
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Select(s => new Choice(s, s)));
            _storage.ItemsSource = storages;
            _storage.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }

        try
        {
            if (await _api.GetNextVmIdAsync() is { } next) _newId.Text = next.ToString();
        }
        catch (Exception ex)
        {
            HardwareLog.Warn($"[복제] 다음 VMID 조회 실패: {ex.Message}");
        }
    }

    private async Task CloneAsync()
    {
        if (_busy) return;

        if (!int.TryParse(_newId.Text, out var newId) || newId < MinVmid)
        {
            SetStatus(Loc.T("CloneWindow_M03", MinVmid));
            return;
        }

        var full = _full.IsChecked == true;
        if (!full && !_guest.IsTemplate)
        {
            SetStatus(Loc.T("CloneWindow_M04"));
            return;
        }

        _busy = true;
        _clone.IsEnabled = false;
        SetStatus(Loc.T("CloneWindow_M05"));

        try
        {
            var target = (_targetNode.SelectedItem as Choice)?.Value ?? string.Empty;
            var storage = full ? (_storage.SelectedItem as Choice)?.Value ?? string.Empty : string.Empty;

            using var flow = WriteConfirmation.Begin(Loc.T("CloneWindow_11"));
            await _api.CloneGuestAsync(
                _guest.Node, _guest.Kind, _guest.VmId, newId,
                string.IsNullOrWhiteSpace(_name.Text) ? null : _name.Text.Trim(),
                full,
                target.Length == 0 ? null : target,
                storage.Length == 0 ? null : storage);
            SetStatus(Loc.T("CloneWindow_M06"));
            await Task.Delay(900);
            // 성공하면 이 화면을 닫고 이전 화면으로 돌아간다(WPF 의 DialogResult = true)
            await AppServices.Navigator.PopAsync();
        }
        catch (WriteCancelledException)
        {
            SetStatus(string.Empty);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("CloneWindow_M07", ex.Message));
        }
        finally
        {
            _busy = false;
            _clone.IsEnabled = true;
        }
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
    }

    /// <summary>콤보 칸 한 줄 — 값(빈 문자열 = 기본)과 보이는 글.</summary>
    private sealed record Choice(string Value, string Label)
    {
        public override string ToString() => Label;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Guest.Tabs;

/// <summary>
///     방화벽 탭(코드로 만든 화면) — 위는 사용 여부, 아래는 규칙 표(<see cref="FirewallRules" />: 추가·복사·보안 그룹·수정·삭제·이동).
///     데이터센터·노드·게스트·보안 그룹이 범위만 달리해 함께 쓴다.
/// </summary>
public sealed class FirewallTab : UserControl
{
    private readonly ProxmoxApiClient _api;
    private readonly FirewallScope _scope;
    private readonly CheckBox _toggle = new() { Content = L.T("FirewallWindow_03"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap };
    private readonly DockPanel _optionsRow = new() { Margin = new Thickness(0, 0, 0, 10) };
    private bool _busy;
    private bool _loaded;

    /// <param name="canEdit">편집 권한 — 없으면 규칙 목록만 보이고 사용 여부도 바꿀 수 없다.</param>
    public FirewallTab(ProxmoxApiClient api, FirewallScope scope, bool canEdit = true)
    {
        _api = api;
        _scope = scope;

        var apply = ActionBar.IconButton(L.T("FirewallWindow_02"), "IconCheck", this);
        apply.Click += async (_, _) => await ApplyAsync();
        DockPanel.SetDock(apply, Dock.Right);
        _optionsRow.Children.Add(apply);
        _optionsRow.Children.Add(_toggle);
        _optionsRow.IsVisible = scope.HasOptions;
        _optionsRow.IsEnabled = canEdit;

        DockPanel.SetDock(_optionsRow, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        var rules = new ContentControl { Content = FirewallRules.Create(api, scope, canEdit).Embedded() }; // 이 화면이 이미 여백을 둔다
        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children = { _optionsRow, _status, rules }
        };

        AttachedToVisualTree += async (_, _) =>
        {
            if (_loaded) return;

            _loaded = true;
            await LoadOptionsAsync();
        };
    }

    private async Task LoadOptionsAsync()
    {
        if (!_scope.HasOptions) return;

        try
        {
            var options = await _api.GetFirewallOptionsAsync(_scope);
            _toggle.IsChecked = options.TryGetValue("enable", out var enabled) && enabled == "1";
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("FirewallWindow_M02", ex.Message));
        }
    }

    private async Task ApplyAsync()
    {
        if (_busy) return;

        _busy = true;
        try
        {
            // 서버를 바꾸므로 쓰기 확인을 거친다(취소하면 요청이 나가지 않는다)
            using (WriteConfirmation.Begin(L.T("FirewallWindow_02")))
                await _api.SetFirewallEnabledAsync(_scope, _toggle.IsChecked == true);

            SetStatus(Loc.T("FirewallWindow_M03",
                Loc.T(_toggle.IsChecked == true ? "Firewall_StateEnabled" : "Firewall_StateDisabled")));
        }
        catch (WriteCancelledException)
        {
            // 사용자가 취소 — 아무것도 바뀌지 않았다
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("FirewallWindow_M04", ex.Message));
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
    }
}

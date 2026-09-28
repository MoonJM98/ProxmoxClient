using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>
///     방화벽 탭 — 위는 사용 여부, 아래는 규칙 표(<see cref="FirewallRules" />: 추가·복사·보안 그룹·수정·삭제·이동).
///     데이터센터·노드·게스트·보안 그룹이 범위만 달리해 함께 쓴다.
/// </summary>
public partial class FirewallTab : UserControl
{
    private readonly ProxmoxApiClient _api;
    private readonly FirewallScope _scope;
    private bool _busy;

    /// <param name="canEdit">편집 권한 — 없으면 규칙 목록만 보이고 사용 여부도 바꿀 수 없다.</param>
    public FirewallTab(ProxmoxApiClient api, FirewallScope scope, bool canEdit = true)
    {
        InitializeComponent();
        _api = api;
        _scope = scope;
        OptionsRow.Visibility = scope.HasOptions ? Visibility.Visible : Visibility.Collapsed;
        RulesHost.Content = FirewallRules.Create(api, scope, canEdit);
        OptionsRow.IsEnabled = canEdit;
        Loaded += async (_, _) => await LoadOptionsAsync();
    }

    private async Task LoadOptionsAsync()
    {
        if (!_scope.HasOptions) return;
        try
        {
            var options = await _api.GetFirewallOptionsAsync(_scope);
            FirewallToggle.IsChecked = options.TryGetValue("enable", out var enabled) && enabled == "1";
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("FirewallWindow_M02", ex.Message));
        }
    }

    private async void OnApplyOptions(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _busy = true;
        try
        {
            await _api.SetFirewallEnabledAsync(_scope, FirewallToggle.IsChecked == true);
            SetStatus(Loc.T("FirewallWindow_M03",
                Loc.T(FirewallToggle.IsChecked == true ? "Firewall_StateEnabled" : "Firewall_StateDisabled")));
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
        StatusText.Text = text;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>방화벽 탭 — 사용 여부와 규칙 목록·추가·삭제. 데이터센터·노드·게스트가 범위만 달리해 함께 쓴다.</summary>
public partial class FirewallTab : UserControl
{
    private readonly ProxmoxApiClient _api;
    private readonly FirewallScope _scope;
    private bool _busy;

    public FirewallTab(ProxmoxApiClient api, FirewallScope scope)
    {
        InitializeComponent();
        _api = api;
        _scope = scope;
        ComboChoices.Fill(RuleActionBox, ComboChoices.FirewallActions);
        ComboChoices.Fill(RuleDirectionBox, ComboChoices.FirewallDirections);
        ComboChoices.Fill(RuleProtoBox, ComboChoices.FirewallProtocols);
        RuleActionBox.SelectedIndex = 0;
        RuleDirectionBox.SelectedIndex = 0;
        RuleProtoBox.SelectedIndex = 0;
        OptionsRow.Visibility = scope.HasOptions ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            if (_scope.HasOptions)
            {
                var options = await _api.GetFirewallOptionsAsync(_scope);
                FirewallToggle.IsChecked = options.TryGetValue("enable", out var enabled) && enabled == "1";
            }

            var rules = await _api.GetFirewallRulesAsync(_scope);
            RulesGrid.ItemsSource = rules;
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

    private async void OnAddRule(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var rule = new PveFirewallRule
        {
            Action = ComboChoices.Selected(RuleActionBox),
            Direction = ComboChoices.Selected(RuleDirectionBox),
            Proto = ComboChoices.Selected(RuleProtoBox),
            DPort = RulePortBox.Text.Trim(),
            Source = RuleSourceBox.Text.Trim(),
            Comment = RuleCommentBox.Text.Trim(),
            Enabled = RuleEnabled.IsChecked == true
        };

        _busy = true;
        try
        {
            await _api.AddFirewallRuleAsync(_scope, rule);
            RulePortBox.Clear();
            RuleSourceBox.Clear();
            RuleCommentBox.Clear();
            SetStatus(Loc.T("FirewallWindow_M05"));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("FirewallWindow_M06", ex.Message));
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnRuleRightClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PveFirewallRule rule) RulesGrid.SelectedItem = rule;
    }

    private async void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (_busy || RulesGrid.SelectedItem is not PveFirewallRule rule) return;

        var text = Loc.T("FirewallWindow_M07", rule.Pos, rule.Action, rule.Direction, rule.DPort);
        var title = Loc.T("FirewallWindow_M08");
        var answer = Window.GetWindow(this) is { } owner
            ? ThemedMessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : ThemedMessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _busy = true;
        try
        {
            await _api.DeleteFirewallRuleAsync(_scope, rule.Pos);
            SetStatus(Loc.T("FirewallWindow_M09"));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("FirewallWindow_M10", ex.Message));
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
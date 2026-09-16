using System.Windows;
using System.Windows.Input;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views;

public partial class FirewallWindow : Window
{
    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private bool _busy;

    public FirewallWindow(ProxmoxApiClient api, PveResource guest)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _api = api;
        _guest = guest;
        Title = Loc.T("FirewallWindow_M01", guest.Kind.Label(), guest.VmId, guest.Name);
        ComboChoices.Fill(RuleActionBox, ComboChoices.FirewallActions);
        ComboChoices.Fill(RuleDirectionBox, ComboChoices.FirewallDirections);
        ComboChoices.Fill(RuleProtoBox, ComboChoices.FirewallProtocols);
        RuleActionBox.SelectedIndex = 0;
        RuleDirectionBox.SelectedIndex = 0;
        RuleProtoBox.SelectedIndex = 0;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var options = await _api.GetFirewallOptionsAsync(_guest.Node, _guest.Kind, _guest.VmId);
            FirewallToggle.IsChecked = options.TryGetValue("enable", out var enabled) && enabled == "1";
            var rules = await _api.GetFirewallRulesAsync(_guest.Node, _guest.Kind, _guest.VmId);
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
            await _api.SetFirewallEnabledAsync(_guest.Node, _guest.Kind, _guest.VmId, FirewallToggle.IsChecked == true);
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
            await _api.AddFirewallRuleAsync(_guest.Node, _guest.Kind, _guest.VmId, rule);
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

        var answer = ThemedMessageBox.Show(this,
            Loc.T("FirewallWindow_M07", rule.Pos, rule.Action, rule.Direction, rule.DPort),
            Loc.T("FirewallWindow_M08"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _busy = true;
        try
        {
            await _api.DeleteFirewallRuleAsync(_guest.Node, _guest.Kind, _guest.VmId, rule.Pos);
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
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views;

public partial class CloneWindow : Window
{
    private const int MinVmid = 100;

    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private bool _busy;

    public CloneWindow(ProxmoxApiClient api, PveResource guest, IReadOnlyList<string> nodeNames,
        IReadOnlyList<string> storageNames)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _api = api;
        _guest = guest;
        HeaderText.Text = $"{guest.Kind.Label()} {guest.VmId} — {guest.Name}";
        NewIdBox.Text = (guest.VmId + 1).ToString();

        ComboChoices.Fill(TargetNodeBox, new[] { ("", Loc.T("CloneWindow_M01", guest.Node)) }
            .Concat(nodeNames.Where(n => n != guest.Node).Select(n => (n, n))));
        TargetNodeBox.SelectedIndex = 0;

        ComboChoices.Fill(StorageBox,
            new[] { ("", Loc.T("CloneWindow_M02")) }.Concat(storageNames.Select(s => (s, s))));
        StorageBox.SelectedIndex = 0;

        // 웹 UI 처럼 템플릿은 연결 복제가 기본, 템플릿이 아니면 전체 복제만 가능하므로 모드를 묻지 않는다
        if (guest.IsTemplate) RadioLinked.IsChecked = true;
        else RowMode.Visibility = Visibility.Collapsed;

        Loaded += async (_, _) => await SuggestIdAsync();
    }

    private async Task SuggestIdAsync()
    {
        try
        {
            if (await _api.GetNextVmIdAsync() is { } next) NewIdBox.Text = next.ToString();
        }
        catch (Exception ex)
        {
            App.Log($"[복제] 다음 VMID 조회 실패: {ex.Message}");
        }
    }

    private async void OnClone(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!int.TryParse(NewIdBox.Text, out var newId) || newId < MinVmid)
        {
            SetStatus(Loc.T("CloneWindow_M03", MinVmid));
            return;
        }

        var full = RadioFull.IsChecked == true;
        if (!full && !_guest.IsTemplate)
        {
            SetStatus(Loc.T("CloneWindow_M04"));
            return;
        }

        _busy = true;
        BtnClone.IsEnabled = false;
        SetStatus(Loc.T("CloneWindow_M05"));

        try
        {
            var target = ComboChoices.Selected(TargetNodeBox);
            var storage = full ? ComboChoices.Selected(StorageBox) : string.Empty;
            await _api.CloneGuestAsync(
                _guest.Node, _guest.Kind, _guest.VmId, newId,
                string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim(),
                full,
                target.Length == 0 ? null : target,
                storage.Length == 0 ? null : storage);
            SetStatus(Loc.T("CloneWindow_M06"));
            await Task.Delay(900);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("CloneWindow_M07", ex.Message));
        }
        finally
        {
            _busy = false;
            BtnClone.IsEnabled = true;
        }
    }

    /// <summary>연결 복제는 원본 디스크를 공유하므로 대상 저장소를 고를 수 없다.</summary>
    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (StorageBox is null) return; // InitializeComponent 중(첫 IsChecked) 호출

        StorageBox.IsEnabled = RadioFull.IsChecked == true;
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }
}
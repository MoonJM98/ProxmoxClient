using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api.Versioning;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>
///     설정을 한 줄씩 보여 주고 고를 때마다 대화상자로 고치는 화면 — 웹 UI 의 Options 탭과 같은 방식.
///     바꾼 항목만 서버에 보내므로 건드리지 않은 값은 그대로 남는다.
///     게스트 설정은 대기 중 변경(재시작 후 적용)을 따로 보여 주고 되돌릴 수 있다.
///     읽기·쓰기를 밖에서 받으므로 게스트 설정·Cloud-Init·노드·데이터센터 설정에 모두 쓴다.
/// </summary>
public partial class OptionsTab : UserControl
{
    private readonly Func<Task<GuestPendingConfig>> _load;
    private readonly IReadOnlyList<GuestOption> _options;
    private readonly Func<IReadOnlyList<string>, Task>? _revert;
    private readonly Func<IReadOnlyDictionary<string, string>, Task> _save;
    private bool _busy;
    private GuestPendingConfig _config = GuestPendingConfig.Empty;

    /// <param name="options">보여 줄 설정 항목(호출자가 미리 걸러서 넘긴다).</param>
    /// <param name="load">현재 값·대기 중 변경을 읽어 오는 함수.</param>
    /// <param name="save">바뀐 항목만 저장하는 함수 — 빈 값은 "삭제(서버 기본값)" 를 뜻한다.</param>
    /// <param name="revert">대기 중 변경을 취소하는 함수(게스트 설정만). 없으면 되돌리기 버튼이 없다.</param>
    /// <param name="extraActions">추가 버튼(예: Cloud-Init 이미지 다시 만들기).</param>
    public OptionsTab(
        IReadOnlyList<GuestOption> options,
        Func<Task<GuestPendingConfig>> load,
        Func<IReadOnlyDictionary<string, string>, Task> save,
        Func<IReadOnlyList<string>, Task>? revert = null,
        IReadOnlyList<TableAction>? extraActions = null,
        ApiFeature? target = null)
    {
        InitializeComponent();
        // 저장 요청(target)이 지금 서버에서 모르는 옵션은 목록에 두지 않는다
        _options = options.Where(o => target?.Accepts(o.Key) ?? true).ToList();
        _load = load;
        _save = save;
        _revert = revert;
        BtnRevert.Visibility = revert is null ? Visibility.Collapsed : Visibility.Visible;
        foreach (var action in (extraActions ?? []).Where(a => a.Requires is not { IsAvailable: false }))
            AddExtraButton(action);

        Loaded += async (_, _) => await ReloadAsync();
    }

    /// <summary>대기 중 변경이 없는 설정(노드·데이터센터 옵션 등).</summary>
    public OptionsTab(
        IReadOnlyList<GuestOption> options,
        Func<Task<IReadOnlyDictionary<string, string>>> load,
        Func<IReadOnlyDictionary<string, string>, Task> save,
        ApiFeature? target = null)
        : this(options, async () => GuestPendingConfig.FromConfig(await load()), save, target: target)
    {
    }

    private void AddExtraButton(TableAction action)
    {
        var button = new Button
        {
            Content = Loc.T(action.LabelKey), Margin = new Thickness(0, 0, 8, 4), MinWidth = 96
        };
        if (TryFindResource(action.IconKey) is System.Windows.Media.Geometry icon) IconAssist.SetIcon(button, icon);
        button.Click += async (_, _) => await RunExtraAsync(action);
        ExtraPanel.Children.Add(button);
    }

    private async Task RunExtraAsync(TableAction action)
    {
        if (_busy) return;

        _busy = true;
        try
        {
            if (await action.Run(null, Window.GetWindow(this)) is { } result) StatusText.Text = result;
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("TableTab_ActionFailed", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnReload(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        await ReloadAsync();
    }

    /// <summary>다시 읽는다 — 상태 줄은 건드리지 않아 방금 저장한 결과(성공·실패)가 남는다.</summary>
    private async Task ReloadAsync()
    {
        if (_busy) return;

        _busy = true;
        try
        {
            _config = await _load();
            // 삭제 예정인 키도 줄이 보여야 되돌릴 수 있으므로 현재 값과 반영 후 값을 합쳐 판단한다
            var union = _config.Current.Concat(_config.Effective.Where(kv => !_config.Current.ContainsKey(kv.Key)))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var rows = _options.Where(o => o.VisibleIf?.Invoke(union) ?? true)
                .Select(o => new OptionRow(o, _config)).ToList();
            OptionGrid.ItemsSource = rows;
            PendingColumn.Visibility = rows.Any(r => r.HasPending) ? Visibility.Visible : Visibility.Collapsed;
            UpdateButtons();
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("GuestSettingsWindow_M03", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var row = OptionGrid.SelectedItem as OptionRow;
        BtnEdit.IsEnabled = row is { Option.ReadOnly: false };
        BtnRevert.IsEnabled = row is { HasPending: true };
    }

    private void OnGridDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 머리글·빈 곳을 두 번 누르면 이전에 고른 줄이 열리지 않게 한다
        if ((e.OriginalSource as FrameworkElement)?.DataContext is OptionRow) OnEdit(sender, e);
    }

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_busy || OptionGrid.SelectedItem is not OptionRow row) return;

        if (row.Option.ReadOnly)
        {
            StatusText.Text = Loc.T("GuestOptions_ReadOnly", row.Label);
            return;
        }

        if (AskChanges(row) is not { } changes) return;

        var changed = changes.Where(kv => _config.Effective.GetValueOrDefault(kv.Key, "") != kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (changed.Count == 0)
        {
            StatusText.Text = Loc.T("GuestSettingsWindow_M04");
            return;
        }

        await RunAsync(() => _save(changed), Loc.T("GuestSettingsWindow_M06"), row.Option.Key);
    }

    private async void OnRevert(object sender, RoutedEventArgs e)
    {
        if (_busy || _revert is null || OptionGrid.SelectedItem is not OptionRow { HasPending: true } row) return;

        var keys = row.Option.KeysForPending.Where(_config.PendingKeys.Contains).ToList();
        await RunAsync(() => _revert(keys), Loc.T("OptionsTab_Reverted", row.Label), row.Option.Key);
    }

    /// <summary>저장·되돌리기를 실행하고 다시 읽는다 — 결과 문구는 다시 읽은 뒤에도 남는다.</summary>
    private async Task RunAsync(Func<Task> work, string doneText, string key)
    {
        _busy = true;
        try
        {
            StatusText.Text = Loc.T("GuestSettingsWindow_M05", 1);
            await work();
            StatusText.Text = doneText;
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("AppSettingsWindow_M03", ex.Message);
            App.Log($"[옵션] {key} 저장 실패: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }

        await ReloadAsync();
    }

    /// <summary>편집 창을 띄워 바꿀 설정들을 받는다(취소면 null). 빈 값은 삭제를 뜻한다.</summary>
    private IReadOnlyDictionary<string, string>? AskChanges(OptionRow row)
    {
        var option = row.Option;
        var owner = Window.GetWindow(this);
        // 설정에 없는 항목은 서버 기본값으로 편집을 시작한다
        var current = row.EffectiveRaw.Length > 0 ? row.EffectiveRaw : option.DefaultValue ?? string.Empty;

        if (option.Editor is { } editor)
        {
            var form = new FormDialog(Loc.T("GuestOptions_EditTitle", row.Label),
                editor.Fields(current, _config.Effective), editor.Validate) { Owner = owner };
            return form.ShowDialog() == true && form.Result is { } values
                ? editor.Build(values, row.EffectiveRaw)
                : null;
        }

        var dialog = new OptionEditDialog(option, current, _config.Effective) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } value) return null;

        // 기본값을 고르면 값 대신 삭제(웹 UI 의 deleteDefaultValue)
        var send = option.DeleteDefault && value == option.DefaultValue ? string.Empty : value;
        return new Dictionary<string, string>(StringComparer.Ordinal) { [option.Key] = send };
    }
}

using System.Globalization;
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
///     설정을 한 줄씩 보여 주고 고를 때마다 편집 시트로 고치는 화면 — 웹 UI 의 Options 탭과 같은 방식.
///     바꾼 항목만 서버에 보내므로 건드리지 않은 값은 그대로 남는다.
///     게스트 설정은 대기 중 변경(재시작 후 적용)을 따로 보여 주고 되돌릴 수 있다.
///     읽기·쓰기를 밖에서 받으므로 게스트 설정·Cloud-Init·노드·데이터센터 설정에 모두 쓴다.
/// </summary>
public partial class OptionsTab : UserControl
{
    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new() { Key = "label", HeaderKey = "MainWindow_40", Width = 200 },
        new() { Key = "display", HeaderKey = "DeviceTable_Value", Width = 0 },
        new() { Key = "pending", HeaderKey = "OptionsTab_Pending", Width = 240 }
    ];

    private readonly IReadOnlyList<GuestOption> _options;
    private readonly Func<Task<GuestPendingConfig>> _load;
    private readonly Func<IReadOnlyDictionary<string, string>, Task> _save;
    private readonly Func<IReadOnlyList<string>, Task>? _revert;
    private readonly TableTab _table;
    private readonly TextBlock _lockText;
    private readonly Border _lockPanel;
    private GuestPendingConfig _config = GuestPendingConfig.Empty;
    private IReadOnlyList<OptionRow> _rows = [];
    private string? _lockReason;

    /// <summary>
    ///     읽어 온 설정을 보고 이 화면을 쓸 수 없는 이유를 돌려준다(쓸 수 있으면 null) — 예: Cloud-Init 드라이브가 없으면
    ///     웹 UI 처럼 항목을 고칠 수 없다.
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>, string?>? LockedReason { get; set; }

    /// <param name="options">보여 줄 설정 항목(호출자가 미리 걸러서 넘긴다).</param>
    /// <param name="load">현재 값·대기 중 변경을 읽어 오는 함수.</param>
    /// <param name="save">바뀐 항목만 저장하는 함수 — 빈 값은 "삭제(서버 기본값)" 를 뜻한다.</param>
    /// <param name="revert">대기 중 변경을 취소하는 함수(게스트 설정만). 없으면 되돌리기 버튼이 없다.</param>
    /// <param name="extraActions">추가 버튼(예: Cloud-Init 이미지 다시 만들기).</param>
    /// <param name="target">저장 요청(target)이 지금 서버에서 모르는 옵션은 목록에 두지 않는다.</param>
    public OptionsTab(
        IReadOnlyList<GuestOption> options,
        Func<Task<GuestPendingConfig>> load,
        Func<IReadOnlyDictionary<string, string>, Task> save,
        Func<IReadOnlyList<string>, Task>? revert = null,
        IReadOnlyList<TableAction>? extraActions = null,
        ApiFeature? target = null)
    {
        _options = options.Where(o => target?.Accepts(o.Key) ?? true).ToList();
        _load = load;
        _save = save;
        _revert = revert;

        var actions = new List<TableAction> { EditAction() };
        if (revert is not null) actions.Add(RevertAction());
        actions.AddRange((extraActions ?? []).Where(a => a.Requires is not { IsAvailable: false }));

        _table = new TableTab(LoadRowsAsync, Columns, "OptionsTab_Hint", actions,
            open: (row, owner) => _ = OpenAsync(row));

        _lockText = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        _lockPanel = new Border
        {
            Classes = { "card" }, MaxWidth = 360, Margin = new Thickness(16), IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = _lockText
        };
        Content = new Grid { Children = { _table, _lockPanel } };
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

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> LoadRowsAsync()
    {
        _config = await _load();

        // 삭제 예정인 키도 줄이 보여야 되돌릴 수 있으므로 현재 값과 반영 후 값을 합쳐 판단한다
        var union = _config.Current.Concat(_config.Effective.Where(kv => !_config.Current.ContainsKey(kv.Key)))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        _rows = _options.Where(o => o.VisibleIf?.Invoke(union) ?? true)
            .Select(o => new OptionRow(o, _config))
            .ToList();
        ApplyLock(LockedReason?.Invoke(union));

        return _rows.Select((row, index) => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["index"] = index.ToString(CultureInfo.InvariantCulture),
            ["label"] = row.Label,
            ["display"] = row.Display,
            ["pending"] = row.PendingText
        }).ToList();
    }

    private void ApplyLock(string? reason)
    {
        _lockReason = reason;
        _lockText.Text = reason ?? string.Empty;
        _lockPanel.IsVisible = reason is not null;
        _table.IsEnabled = reason is null;
    }

    private TableAction EditAction()
    {
        return new TableAction
        {
            LabelKey = "ServerManagerWindow_03", IconKey = "IconPencil", NeedsSelection = true,
            Run = (row, _) => row is null || !TryRow(row, out var item)
                ? Task.FromResult<string?>(null)
                : EditAsync(item)
        };
    }

    private TableAction RevertAction()
    {
        return new TableAction
        {
            LabelKey = "OptionsTab_Revert", IconKey = "IconUndo", NeedsSelection = true,
            Run = (row, _) => row is null || !TryRow(row, out var item) || !item.HasPending || _revert is null
                ? Task.FromResult<string?>(null)
                : RevertAsync(item)
        };
    }

    /// <summary>줄을 두 번 누르면 편집 — 목록 버튼 "편집" 과 같은 흐름(확인 문구는 항목 이름).</summary>
    private async Task OpenAsync(IReadOnlyDictionary<string, string> row)
    {
        if (!TryRow(row, out var item) || _lockReason is not null) return;

        string? message;
        try
        {
            using (WriteConfirmation.Begin(item.Label))
                message = await EditAsync(item);
        }
        catch (WriteCancelledException)
        {
            return;
        }
        catch (Exception ex)
        {
            await Ask.ErrorAsync(ErrorText.Describe(ex));
            return;
        }

        if (message is null) return;

        await _table.ReloadAsync();
        AppServices.Dialogs.Toast(message);
    }

    /// <summary>편집 시트를 띄워 바꿀 설정을 받고 저장한다. 바뀐 것이 없으면 그렇게 알리고, 취소면 null.</summary>
    private async Task<string?> EditAsync(OptionRow item)
    {
        if (_lockReason is not null) return null;

        var option = item.Option;
        if (option.ReadOnly)
        {
            await Ask.InfoAsync(Loc.T("GuestOptions_ReadOnly", item.Label));
            return null;
        }

        if (await AskChangesAsync(item) is not { } changes) return null;

        var changed = changes.Where(kv => _config.Effective.GetValueOrDefault(kv.Key, "") != kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (changed.Count == 0) return Loc.T("GuestSettingsWindow_M04");

        await _save(changed);
        return Loc.T("GuestSettingsWindow_M06");
    }

    private async Task<string?> RevertAsync(OptionRow item)
    {
        var keys = item.Option.KeysForPending.Where(_config.PendingKeys.Contains).ToList();
        await _revert!(keys);
        return Loc.T("OptionsTab_Reverted", item.Label);
    }

    /// <summary>입력 칸(여러 칸 편집기)이 있으면 그 폼을, 없으면 한 칸 시트를 띄운다. 취소면 null.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> AskChangesAsync(OptionRow item)
    {
        var option = item.Option;
        // 설정에 없는 항목은 서버 기본값으로 편집을 시작한다
        var current = item.EffectiveRaw.Length > 0 ? item.EffectiveRaw : option.DefaultValue ?? string.Empty;

        if (option.Editor is { } editor)
        {
            var values = await FormDialog.ShowAsync(Loc.T("GuestOptions_EditTitle", item.Label),
                editor.Fields(current, _config.Effective), editor.Validate);
            return values is null ? null : editor.Build(values, item.EffectiveRaw);
        }

        if (await OptionEditDialog.ShowAsync(option, current, _config.Effective) is not { } value) return null;

        // 기본값을 고르면 값 대신 삭제(웹 UI 의 deleteDefaultValue)
        var send = option.DeleteDefault && value == option.DefaultValue ? string.Empty : value;
        return new Dictionary<string, string>(StringComparer.Ordinal) { [option.Key] = send };
    }

    /// <summary>표의 줄(행 번호 "index")을 이 탭이 만든 옵션 줄로 되돌린다.</summary>
    private bool TryRow(IReadOnlyDictionary<string, string> row, out OptionRow item)
    {
        if (int.TryParse(row.GetValueOrDefault("index"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var index) && index >= 0 && index < _rows.Count)
        {
            item = _rows[index];
            return true;
        }

        item = null!;
        return false;
    }
}

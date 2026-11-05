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
///     게스트 스냅숏 탭 — 목록(설명 편집·롤백·삭제)과 아래의 새 스냅숏 칸. 되돌리기·삭제는 확인 후 작업으로 돈다.
/// </summary>
public sealed class SnapshotsTab : UserControl
{
    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new() { Key = "name", HeaderKey = "MainWindow_40", Width = 160 },
        new() { Key = "created", HeaderKey = "SnapshotDialog_02", Width = 170 },
        new() { Key = "ram", HeaderKey = "SnapshotDialog_03", Width = 90, Format = TableFormats.Flag },
        new() { Key = "description", HeaderKey = "FirewallWindow_13", Width = 0 }
    ];

    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private readonly TableTab _table;
    private readonly TextBox _newName = new() { PlaceholderText = Loc.T("Snapshot_Name") };
    private readonly TextBox _newDescription = new() { PlaceholderText = Loc.T("FirewallWindow_13") };
    private readonly CheckBox _includeRam = new() { Content = Loc.T("SnapshotDialog_09") };
    private readonly Button _create;
    private readonly TextBlock _status = new() { Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 8, 16, 10) };

    public SnapshotsTab(ProxmoxApiClient api, PveResource guest)
    {
        _api = api;
        _guest = guest;

        _table = new TableTab(LoadAsync, Columns, "SnapshotsTab_Hint",
        [
            new TableAction
            {
                LabelKey = "SnapshotDialog_EditNote", IconKey = "IconPencil", NeedsSelection = true,
                Run = EditNoteAsync
            },
            new TableAction
            {
                LabelKey = "SnapshotDialog_04", IconKey = "IconUndo", NeedsSelection = true,
                Confirm = row => Loc.T("SnapshotDialog_M03", ActionHelpers.Value(row!, "name")),
                Run = (row, _) => row is null
                    ? Task.FromResult<string?>(null)
                    : RunSnapshotAsync(() => _api.RollbackSnapshotAsync(_guest.Node, _guest.Kind, _guest.VmId,
                        ActionHelpers.Value(row, "name")), Loc.T("SnapshotDialog_ActionRollback", ActionHelpers.Value(row, "name")))
            },
            new TableAction
            {
                LabelKey = "SnapshotDialog_05", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => Loc.T("SnapshotDialog_M06", ActionHelpers.Value(row!, "name")),
                Run = (row, _) => row is null
                    ? Task.FromResult<string?>(null)
                    : RunSnapshotAsync(() => _api.DeleteSnapshotAsync(_guest.Node, _guest.Kind, _guest.VmId,
                        ActionHelpers.Value(row, "name")), Loc.T("SnapshotDialog_ActionDelete", ActionHelpers.Value(row, "name")))
            }
        ]);

        _create = ActionBar.IconButton(Loc.T("SnapshotDialog_08"), "IconCamera", this);
        _create.Classes.Add("accent");
        _create.Click += async (_, _) => await CreateAsync();

        var form = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = Loc.T("SnapshotDialog_06"), Classes = { "section" } },
                new TextBlock { Text = Loc.T("MainWindow_40"), Classes = { "label" } },
                _newName,
                new TextBlock { Text = Loc.T("FirewallWindow_13"), Classes = { "label" } },
                _newDescription,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 4, 0, 0),
                    Children = { _includeRam, _create }
                }
            }
        };

        var card = new Border { Classes = { "card" }, Margin = new Thickness(12, 0, 12, 0), Child = form };
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        Grid.SetRow(_table, 0);
        Grid.SetRow(card, 1);
        Grid.SetRow(_status, 2);
        grid.Children.Add(_table);
        grid.Children.Add(card);
        grid.Children.Add(_status);
        Content = grid;
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> LoadAsync()
    {
        var snapshots = await _api.GetSnapshotsAsync(_guest.Node, _guest.Kind, _guest.VmId);
        // "current" 는 현재 상태를 나타내는 가상 항목 — 되돌리거나 지울 수 없다
        var rows = snapshots.Where(s => s.Name != "current")
            .Select(s => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = s.Name,
                ["created"] = s.SnapTimeUtc is { } time ? Format.LocalTime(time) : "-",
                ["ram"] = s.HasRam ? TableFormats.CheckValue : string.Empty,
                ["description"] = s.Description ?? string.Empty
            })
            .ToList();
        _status.Text = rows.Count == 0 ? Loc.T("SnapshotDialog_Empty") : string.Empty;
        return rows;
    }

    /// <summary>설명(메모)만 바꾼다 — 스냅숏 내용은 그대로라 작업 없이 바로 끝난다.</summary>
    private async Task<string?> EditNoteAsync(IReadOnlyDictionary<string, string>? row, Control? owner)
    {
        if (row is null) return null;

        var name = ActionHelpers.Value(row, "name");
        var values = await FormDialog.ShowAsync(Loc.T("SnapshotDialog_EditNoteTitle", name),
        [
            new FormField
            {
                Key = "description", LabelKey = "FirewallWindow_13", Kind = FormFieldKind.Multiline,
                Initial = ActionHelpers.Value(row, "description"), CanLoadFile = false
            }
        ]);
        if (values is null) return null;

        await _api.UpdateSnapshotDescriptionAsync(_guest.Node, _guest.Kind, _guest.VmId, name,
            values.GetValueOrDefault("description") ?? string.Empty);
        return Loc.T("SnapshotDialog_NoteSaved", name);
    }

    /// <summary>작업을 시작하고 끝날 때까지 기다린 뒤 결과 문구를 돌려준다(실패도 문구로 — 화면 상태 줄에 보인다).</summary>
    private async Task<string?> RunSnapshotAsync(Func<Task<string>> start, string label)
    {
        var upid = await start();
        if (upid.Length == 0) return Loc.T("SnapshotDialog_Done", label);

        var task = await _api.WaitTaskAsync(upid);
        return task.IsOk ? Loc.T("SnapshotDialog_Done", label) : Loc.T("SnapshotDialog_Failed", label, task.Status);
    }

    private async Task CreateAsync()
    {
        var name = _newName.Text?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Contains(' '))
        {
            await Ask.InfoAsync(Loc.T("SnapshotDialog_M01"), Loc.T("ProfileDialog_M08"));
            return;
        }

        var label = Loc.T("SnapshotDialog_ActionCreate", name);
        var description = _newDescription.Text?.Trim();
        _create.IsEnabled = false;
        try
        {
            using (WriteConfirmation.Begin(label))
            {
                _status.Text = Loc.T("SnapshotDialog_Running", label);
                _status.Text = await RunSnapshotAsync(
                    () => _api.CreateSnapshotAsync(_guest.Node, _guest.Kind, _guest.VmId, name,
                        string.IsNullOrWhiteSpace(description) ? null : description, _includeRam.IsChecked == true),
                    label) ?? string.Empty;
            }

            _newName.Text = string.Empty;
            _newDescription.Text = string.Empty;
            _includeRam.IsChecked = false;
            await _table.ReloadAsync();
        }
        catch (WriteCancelledException)
        {
            _status.Text = string.Empty;
        }
        catch (Exception ex)
        {
            _status.Text = Loc.T("SnapshotDialog_Failed", label, ex.Message);
        }
        finally
        {
            _create.IsEnabled = true;
        }
    }
}

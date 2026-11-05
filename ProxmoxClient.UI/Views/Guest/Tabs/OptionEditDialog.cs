using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Guest.Tabs;

/// <summary>
///     옵션 한 항목을 고치는 시트 — 항목 종류에 맞는 입력만 보인다(WPF OptionEditDialog 와 같은 입력).
///     <see cref="FormDialog" /> 과 같은 모양으로 화면 위에 띄우고, 확인하면 값을 돌려준다(취소면 null).
/// </summary>
public sealed class OptionEditDialog : UserControl, IDismissible
{
    private static readonly IReadOnlyDictionary<string, string> NoConfig = new Dictionary<string, string>();

    private readonly GuestOption _option;
    private readonly string _currentValue;
    private readonly List<BootItem> _boot = [];
    private readonly StackPanel _body = new() { Spacing = 10 };
    private readonly StackPanel _bootRows = new() { Spacing = 4 };
    private readonly TextBlock _hint = new()
    {
        Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap, IsVisible = false
    };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private CheckBox? _check;
    private ComboBox? _combo;
    private ComboBox? _family;
    private ComboBox? _version;
    private Control? _versionRow;
    private TextBox? _text;
    private string? _result;

    private OptionEditDialog(GuestOption option, string currentValue, IReadOnlyDictionary<string, string> config)
    {
        _option = option;
        _currentValue = currentValue;
        var label = Loc.T(option.LabelKey);

        switch (option.Kind)
        {
            case OptionKind.Bool:
                _check = new CheckBox { Content = label, IsChecked = currentValue == "1" };
                _body.Children.Add(_check);
                break;

            case OptionKind.Choice:
                _combo = AddChoice(label, option.Choices ?? []);
                ComboChoices.Select(_combo, currentValue);
                break;

            case OptionKind.OsType:
                ShowOsType(currentValue);
                break;

            case OptionKind.BootOrder:
                ShowBootOrder(currentValue, config);
                break;

            default:
                _text = new TextBox { Text = currentValue };
                AddLabeled(label, _text);
                break;
        }

        if (option.EmptyLabelKey is { } hintKey) ShowHint(Loc.T("GuestOptions_EmptyHint", Loc.T(hintKey)));

        var ok = new Button { Content = Loc.T("Common_Ok"), MinWidth = 88, MinHeight = 40, Classes = { "accent" } };
        ok.Click += (_, _) => Submit();
        var cancel = new Button { Content = Loc.T("Common_Cancel"), MinWidth = 88, MinHeight = 40 };
        cancel.Click += (_, _) => Dismiss();

        Content = new DockPanel
        {
            Children =
            {
                Docked(new TextBlock
                {
                    Text = Loc.T("GuestOptions_EditTitle", label), Classes = { "headline" },
                    Margin = new Thickness(20, 18, 20, 8)
                }, Dock.Top),
                Docked(new StackPanel
                {
                    Margin = new Thickness(20, 8, 20, 16), Spacing = 10,
                    Children =
                    {
                        _error,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { cancel, ok }
                        }
                    }
                }, Dock.Bottom),
                new ScrollViewer { MaxHeight = 520, Padding = new Thickness(20, 4, 20, 4), Content = _body }
            }
        };
    }

    /// <summary>
    ///     옵션 한 항목을 편집 시트로 묻는다. 확인하면 새 값(부팅 순서는 "order=…"), 취소하면 null.
    ///     빈 값은 서버에서 지운다는 뜻이 아니다 — 기본값 처리는 호출자가 한다.
    /// </summary>
    public static async Task<string?> ShowAsync(GuestOption option, string currentValue,
        IReadOnlyDictionary<string, string>? config)
    {
        var dialog = new OptionEditDialog(option, currentValue, config ?? NoConfig);
        await AppServices.Dialogs.ShowSheetAsync(dialog);
        return dialog._result;
    }

    public void Dismiss()
    {
        _result = null;
        AppServices.Dialogs.CloseSheet();
    }

    private static T Docked<T>(T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private void AddLabeled(string label, Control input)
    {
        _body.Children.Add(new TextBlock { Text = label, Classes = { "label" } });
        _body.Children.Add(input);
    }

    private ComboBox AddChoice(string label, IEnumerable<(string Value, string Label)> choices)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        ComboChoices.Fill(combo, choices);
        AddLabeled(label, combo);
        return combo;
    }

    private void ShowHint(string text)
    {
        if (!_body.Children.Contains(_hint)) _body.Children.Add(_hint);
        _hint.Text = text;
        _hint.IsVisible = true;
    }

    /// <summary>OS 유형 — 유형을 고르면 그 유형의 버전 목록이 바뀐다(웹 UI 와 같다).</summary>
    private void ShowOsType(string currentValue)
    {
        _family = AddChoice(Loc.T("OsType_Type"),
            OsTypes.Families.Select(f => (f.LabelKey, f.LabelKey)));
        ComboChoices.Select(_family, OsTypes.FamilyOf(currentValue).LabelKey);

        _version = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var versionLabel = new TextBlock { Text = Loc.T("OsType_Version"), Classes = { "label" } };
        _versionRow = new StackPanel { Spacing = 4, Children = { versionLabel, _version } };
        _body.Children.Add(_versionRow);

        _family.SelectionChanged += (_, _) => FillVersions();
        FillVersions();
    }

    private void FillVersions()
    {
        if (_family is null || _version is null || _versionRow is null) return;

        var family = OsTypes.Families.FirstOrDefault(f => f.LabelKey == ComboChoices.Selected(_family));
        if (family is null) return;

        // 지금 값이 이 유형에 있으면 그대로, 아니면(유형을 바꿨으면) 첫 버전
        var versions = OsTypes.VersionsFor(family, _currentValue);
        var wanted = string.IsNullOrEmpty(_currentValue) ? OsTypes.DefaultValue : _currentValue;
        ComboChoices.Fill(_version, versions);
        if (versions.Any(v => v.Value == wanted)) ComboChoices.Select(_version, wanted);
        else _version.SelectedIndex = 0;
        _versionRow.IsVisible = OsTypes.HasVersionChoice(family);
    }

    private void ShowBootOrder(string currentValue, IReadOnlyDictionary<string, string> config)
    {
        foreach (var device in BootOrder.Parse(currentValue, config))
            _boot.Add(new BootItem { Name = device.Name, Description = device.Description, Enabled = device.Enabled });

        _body.Children.Add(_bootRows);
        RenderBoot();
        ShowHint(Loc.T("BootOrder_Hint"));
    }

    /// <summary>부팅 목록을 다시 그린다 — 줄마다 체크·위·아래(모바일에서도 손가락으로 순서를 바꾼다).</summary>
    private void RenderBoot()
    {
        _bootRows.Children.Clear();
        for (var index = 0; index < _boot.Count; index++)
        {
            var item = _boot[index];
            var position = index;
            var check = new CheckBox
            {
                IsChecked = item.Enabled,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = item.Name },
                        new TextBlock { Text = item.Description, Classes = { "subtle" } }
                    }
                }
            };
            check.IsCheckedChanged += (_, _) => item.Enabled = check.IsChecked == true;

            var up = ActionBar.IconButton(Loc.T("BootOrder_Up"), "IconArrowUp", this, iconOnly: true);
            up.IsEnabled = index > 0;
            up.Click += (_, _) => MoveBoot(position, -1);
            var down = ActionBar.IconButton(Loc.T("BootOrder_Down"), "IconArrowDown", this, iconOnly: true);
            down.IsEnabled = index < _boot.Count - 1;
            down.Click += (_, _) => MoveBoot(position, 1);

            var row = new DockPanel
            {
                Children =
                {
                    Docked(new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 2, Children = { up, down }
                    }, Dock.Right),
                    check
                }
            };
            _bootRows.Children.Add(row);
        }
    }

    private void MoveBoot(int index, int offset)
    {
        var target = index + offset;
        if (target < 0 || target >= _boot.Count) return;

        (_boot[index], _boot[target]) = (_boot[target], _boot[index]);
        RenderBoot();
    }

    private void Submit()
    {
        if (_option.Kind == OptionKind.BootOrder && !_boot.Any(i => i.Enabled))
        {
            _error.Text = Loc.T("BootOrder_NeedOne");
            return;
        }

        _result = _option.Kind switch
        {
            OptionKind.Bool => _check?.IsChecked == true ? "1" : "0",
            OptionKind.Choice => ComboChoices.Selected(_combo!),
            OptionKind.OsType => ComboChoices.Selected(_version!),
            OptionKind.BootOrder => BootOrder.Format(
                _boot.Select(i => new BootDevice(i.Name, i.Description, i.Enabled))),
            _ => _text?.Text?.Trim() ?? string.Empty
        };
        AppServices.Dialogs.CloseSheet();
    }

    /// <summary>부팅 목록 한 줄의 편집 상태 — 체크 상태만 바뀌고 확인 때 <see cref="BootDevice" /> 로 옮긴다.</summary>
    private sealed class BootItem
    {
        public required string Name { get; init; }

        public required string Description { get; init; }

        public bool Enabled { get; set; }
    }
}

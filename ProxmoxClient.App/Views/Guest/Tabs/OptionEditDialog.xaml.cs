using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>옵션 한 항목을 고치는 작은 대화상자 — 항목 유형에 맞는 입력만 보여 준다.</summary>
public partial class OptionEditDialog : Window
{
    private readonly ObservableCollection<BootItem> _bootItems = [];
    private readonly string _currentValue;
    private readonly GuestOption _option;

    /// <param name="config">게스트 설정 전체 — 부팅 순서처럼 다른 항목(장치 목록)을 봐야 하는 편집에 쓴다.</param>
    public OptionEditDialog(GuestOption option, string currentValue,
        IReadOnlyDictionary<string, string>? config = null)
    {
        _option = option;
        _currentValue = currentValue;
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);

        var label = Loc.T(option.LabelKey);
        Title = Loc.T("GuestOptions_EditTitle", label);

        switch (option.Kind)
        {
            case OptionKind.Bool:
                ValueCheck.Visibility = Visibility.Visible;
                ValueCheck.Content = label;
                ValueCheck.IsChecked = currentValue == "1";
                break;

            case OptionKind.Choice:
                ChoiceRow.Visibility = Visibility.Visible;
                ChoiceRow.Label = label;
                ComboChoices.Fill(ValueCombo, option.Choices ?? []);
                ComboChoices.Select(ValueCombo, currentValue);
                break;

            case OptionKind.OsType:
                ShowOsType(currentValue);
                return;

            case OptionKind.BootOrder:
                ShowBootOrder(currentValue, config ?? new Dictionary<string, string>());
                return;

            default:
                TextRow.Visibility = Visibility.Visible;
                TextRow.Label = label;
                ValueBox.Text = currentValue;
                ValueBox.Focus();
                ValueBox.SelectAll();
                break;
        }

        if (option.EmptyLabelKey is { } hintKey) ShowHint(Loc.T("GuestOptions_EmptyHint", Loc.T(hintKey)));
    }

    /// <summary>확인을 눌렀을 때의 값(취소면 null).</summary>
    public string? Result { get; private set; }

    private void ShowHint(string text)
    {
        HintText.Visibility = Visibility.Visible;
        HintText.Text = text;
    }

    /// <summary>OS 유형 — 웹 UI 처럼 유형(Linux·Windows…)을 고르면 그 유형의 버전 목록이 바뀐다.</summary>
    private void ShowOsType(string currentValue)
    {
        ChoiceRow.Visibility = Visibility.Visible;
        ChoiceRow.Label = Loc.T("OsType_Type");
        ComboChoices.Fill(ValueCombo, OsTypes.Families.Select(f => (f.LabelKey, f.LabelKey)));
        ComboChoices.Select(ValueCombo, OsTypes.FamilyOf(currentValue).LabelKey); // 버전 목록은 OnChoiceChanged 가 채운다
    }

    private void OnChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_option.Kind != OptionKind.OsType) return;

        var family = OsTypes.Families.FirstOrDefault(f => f.LabelKey == ComboChoices.Selected(ValueCombo));
        if (family is null) return;

        // 지금 값이 이 유형에 있으면 그대로, 아니면(유형을 바꿨으면) 첫 버전
        var versions = OsTypes.VersionsFor(family, _currentValue);
        var wanted = string.IsNullOrEmpty(_currentValue) ? OsTypes.DefaultValue : _currentValue;
        ComboChoices.Fill(VersionCombo, versions);
        if (versions.Any(v => v.Value == wanted)) ComboChoices.Select(VersionCombo, wanted);
        else VersionCombo.SelectedIndex = 0;
        VersionRow.Visibility = OsTypes.HasVersionChoice(family) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowBootOrder(string currentValue, IReadOnlyDictionary<string, string> config)
    {
        foreach (var device in BootOrder.Parse(currentValue, config))
            _bootItems.Add(new BootItem
            {
                Name = device.Name, Description = device.Description, Enabled = device.Enabled
            });

        BootList.ItemsSource = _bootItems;
        BootPanel.Visibility = Visibility.Visible;
        ShowHint(Loc.T("BootOrder_Hint"));
    }

    private void OnBootUp(object sender, RoutedEventArgs e)
    {
        MoveSelected(-1);
    }

    private void OnBootDown(object sender, RoutedEventArgs e)
    {
        MoveSelected(1);
    }

    private void MoveSelected(int offset)
    {
        var index = BootList.SelectedIndex;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _bootItems.Count) return;

        _bootItems.Move(index, target);
        BootList.SelectedIndex = target;
        BootList.ScrollIntoView(BootList.SelectedItem);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_option.Kind == OptionKind.BootOrder && !_bootItems.Any(i => i.Enabled))
        {
            ShowHint(Loc.T("BootOrder_NeedOne"));
            return;
        }

        Result = _option.Kind switch
        {
            OptionKind.Bool => ValueCheck.IsChecked == true ? "1" : "0",
            OptionKind.Choice => ComboChoices.Selected(ValueCombo),
            OptionKind.OsType => ComboChoices.Selected(VersionCombo),
            OptionKind.BootOrder => BootOrder.Format(
                _bootItems.Select(i => new BootDevice(i.Name, i.Description, i.Enabled))),
            _ => ValueBox.Text.Trim()
        };
        DialogResult = true;
    }

    /// <summary>부팅 목록 한 줄의 편집 상태 — 체크박스가 직접 바꾼다(확인 때 <see cref="BootDevice" />로 옮긴다).</summary>
    public sealed class BootItem
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public bool Enabled { get; set; }
    }
}

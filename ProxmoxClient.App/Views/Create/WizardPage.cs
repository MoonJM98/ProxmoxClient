using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views.Create;

/// <summary>
///     마법사 단계 화면을 채우는 도우미 — 칸마다 읽기(get)·쓰기(set)를 받아 입력을 바로 모델에 반영한다.
///     <c>rebuild</c> 를 켠 칸은 값이 바뀌면 단계를 다시 그린다(OS 유형처럼 다른 칸에 영향을 주는 값).
///     고급(advanced) 칸은 창 아래 "고급" 이 켜졌을 때만 그린다.
/// </summary>
internal sealed class WizardPage(StackPanel panel, bool showAdvanced, WizardWindow window)
{
    private const double LabelWidth = 170;

    public WizardWindow Window { get; } = window;

    private bool Skip(bool advanced)
    {
        return advanced && !showAdvanced;
    }

    private void Row(string labelKey, UIElement input, string? hint)
    {
        panel.Children.Add(new FormRow
        {
            Label = Loc.T(labelKey), LabelWidth = LabelWidth, Content = input, Hint = hint
        });
    }

    public void Section(string labelKey)
    {
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T(labelKey), Style = (Style)Application.Current.FindResource("FormSection"),
            Margin = new Thickness(0, 10, 0, 6)
        });
    }

    /// <summary>한 번 읽을 설명 — 폼을 늘이지 않게 (i) 아이콘 툴팁으로 둔다.</summary>
    public void Note(string text)
    {
        panel.Children.Add(new InfoTip { Text = text, Margin = new Thickness(0, 2, 0, 8) });
    }

    public void Text(string labelKey, Func<string> get, Action<string> set, string? hint = null, bool advanced = false)
    {
        if (Skip(advanced)) return;
        var box = new TextBox { Text = get() };
        box.TextChanged += (_, _) => set(box.Text);
        Row(labelKey, box, hint);
    }

    public void Password(string labelKey, Func<string> get, Action<string> set, string? hint = null)
    {
        var box = new PasswordBox { Password = get() };
        box.PasswordChanged += (_, _) => set(box.Password);
        Row(labelKey, box, hint);
    }

    public void Multiline(string labelKey, Func<string> get, Action<string> set, string? hint = null)
    {
        var box = new TextBox
        {
            Text = get(), AcceptsReturn = true, Height = 90, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas")
        };
        box.TextChanged += (_, _) => set(box.Text);
        Row(labelKey, box, hint);
    }

    public void Check(string labelKey, Func<bool> get, Action<bool> set, string? hint = null, bool advanced = false,
        bool rebuild = false, bool enabled = true)
    {
        if (Skip(advanced)) return;
        var box = new CheckBox { IsChecked = get(), IsEnabled = enabled, Margin = new Thickness(0, 6, 0, 0) };
        box.Click += (_, _) =>
        {
            set(box.IsChecked == true);
            if (rebuild) Window.Rebuild();
        };
        Row(labelKey, box, hint);
    }

    /// <summary>선택 칸 — 목록에 없는 현재 값도 잃지 않게 그대로 넣는다.</summary>
    public void Choice(string labelKey, IReadOnlyList<(string Value, string Label)> choices, Func<string> get,
        Action<string> set, string? hint = null, bool advanced = false, bool rebuild = false)
    {
        if (Skip(advanced)) return;
        var combo = new ComboBox();
        ComboChoices.Fill(combo, choices);
        ComboChoices.Select(combo, get());
        // 빈 값이 목록에 없으면 콤보는 첫 항목을 보여 준다 — 보이는 값과 모델이 어긋나지 않게 맞춘다
        if (ComboChoices.Selected(combo) is { Length: > 0 } shown && shown != get()) set(shown);
        combo.SelectionChanged += (_, _) =>
        {
            var value = ComboChoices.Selected(combo);
            if (value == get()) return;
            set(value);
            if (rebuild) Window.Rebuild();
        };
        Row(labelKey, combo, hint);
    }

    public void Button(string labelKey, Action onClick)
    {
        var button = new Button { Content = Loc.T(labelKey), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(LabelWidth + 8, 4, 0, 8), MinWidth = 120 };
        button.Click += (_, _) => onClick();
        panel.Children.Add(button);
    }

    /// <summary>확인 단계의 키·값 표(키 이름순) — 웹 UI 의 Confirm 탭.</summary>
    public void Summary(IReadOnlyDictionary<string, string> values)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, MaxHeight = 360,
            ItemsSource = values.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList()
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = Loc.T("Wizard_Key"), Binding = new System.Windows.Data.Binding("Key"), Width = 160
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = Loc.T("DeviceTable_Value"), Binding = new System.Windows.Data.Binding("Value"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        panel.Children.Add(grid);
    }
}

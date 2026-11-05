using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Views.Guest.Hardware;

namespace ProxmoxClient.UI.Views.Create;

/// <summary>
///     마법사 단계 화면을 채우는 도우미 — 칸마다 읽기(get)·쓰기(set)를 받아 입력을 바로 모델에 반영한다.
///     <c>rebuild</c> 를 켠 칸은 값이 바뀌면 단계를 다시 그린다(OS 유형처럼 다른 칸에 영향을 주는 값).
///     고급(advanced) 칸은 화면 아래 "고급" 이 켜졌을 때만 그린다.
///     칸은 모두 한 열로 쌓는다(칸 이름 · 입력 · 힌트) — 412px 폭에서도 잘리지 않는다.
/// </summary>
internal sealed class WizardPage(StackPanel panel, bool showAdvanced, WizardView view)
{
    public WizardView Window { get; } = view;

    private bool Skip(bool advanced)
    {
        return advanced && !showAdvanced;
    }

    private void Row(string labelKey, Control input, string? hint)
    {
        panel.Children.Add(new HardwareInput.Row(Loc.T(labelKey), input, hint)
        {
            Margin = new Thickness(0, 0, 0, 10)
        });
    }

    public void Section(string labelKey)
    {
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T(labelKey), Classes = { "section" }, Margin = new Thickness(0, 12, 0, 4)
        });
    }

    /// <summary>한 번 읽을 설명 — 폼을 늘이지 않게 칸 위의 작은 글씨로 둔다.</summary>
    public void Note(string text)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text, Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap, FontSize = 13,
            Margin = new Thickness(0, 0, 0, 10)
        });
    }

    public void Text(string labelKey, Func<string> get, Action<string> set, string? hint = null, bool advanced = false)
    {
        if (Skip(advanced)) return;
        var box = new TextBox { Text = get() };
        box.TextChanged += (_, _) => set(box.Text ?? string.Empty);
        Row(labelKey, box, hint);
    }

    public void Password(string labelKey, Func<string> get, Action<string> set, string? hint = null)
    {
        var box = new TextBox { Text = get(), PasswordChar = '•' };
        box.TextChanged += (_, _) => set(box.Text ?? string.Empty);
        Row(labelKey, box, hint);
    }

    public void Multiline(string labelKey, Func<string> get, Action<string> set, string? hint = null)
    {
        var box = new TextBox
        {
            Text = get(), AcceptsReturn = true, MinHeight = 90, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("monospace")
        };
        box.TextChanged += (_, _) => set(box.Text ?? string.Empty);
        Row(labelKey, box, hint);
    }

    public void Check(string labelKey, Func<bool> get, Action<bool> set, string? hint = null, bool advanced = false,
        bool rebuild = false, bool enabled = true)
    {
        if (Skip(advanced)) return;
        var box = new CheckBox { IsChecked = get(), IsEnabled = enabled, Margin = new Thickness(0, 4, 0, 0) };
        box.IsCheckedChanged += (_, _) =>
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
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
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
        var button = new Button
        {
            Content = Loc.T(labelKey), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 10), MinWidth = 120
        };
        button.Click += (_, _) => onClick();
        panel.Children.Add(button);
    }

    /// <summary>확인 단계의 키·값 목록(키 이름순) — 웹 UI 의 Confirm 탭. 칸을 위아래로 쌓아 폭을 넘기지 않는다.</summary>
    public void Summary(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 3) };
            var name = new TextBlock
            {
                Text = key, Classes = { "subtle" }, FontSize = 13, Width = 150, Margin = new Thickness(0, 0, 8, 0),
                TextWrapping = TextWrapping.Wrap
            };
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(text, 1);
            grid.Children.Add(name);
            grid.Children.Add(text);
            panel.Children.Add(grid);
        }
    }
}

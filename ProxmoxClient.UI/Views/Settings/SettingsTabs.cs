using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Views.Settings;

/// <summary>
///     설정 화면의 탭 — WPF 앱 설정은 왼쪽 세로 목록(NavListBox), 콘솔 설정은 위쪽 밑줄 탭(SubTabItem)이다.
///     두 모양 모두 같은 스타일 클래스를 쓰고, 고른 탭만 보인다.
/// </summary>
internal sealed class SettingsTabs : DockPanel
{
    private readonly ListBox _bar = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly Grid _body = new();

    /// <param name="tabs">탭 이름 키와 내용.</param>
    /// <param name="vertical">true: 왼쪽 세로 목록(170px, 데스크톱 앱 설정) · false: 위쪽 밑줄 탭.</param>
    public SettingsTabs(IReadOnlyList<(string LabelKey, Control Panel)> tabs, bool vertical)
    {
        if (vertical)
        {
            _bar.Classes.Add("nav"); // 세로 목록(테마의 nav 는 세로로 쌓는다 — NavListBox 는 위쪽 가로 탭)
            _bar.Width = 170;
            _bar.Background = BrushOf("BrushPanel");
            _bar.BorderBrush = BrushOf("BrushBorder");
            _bar.BorderThickness = new Thickness(0, 0, 1, 0);
            DockPanel.SetDock(_bar, Dock.Left);
            Children.Add(_bar);
        }
        else
        {
            _bar.Classes.Add("SubTabs");
            _bar.Margin = new Thickness(20, 12, 20, 0);
            _bar.ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel());
            _bar.BorderBrush = BrushOf("BrushBorder");
            _bar.BorderThickness = new Thickness(0, 0, 0, 1);
            DockPanel.SetDock(_bar, Dock.Top);
            Children.Add(_bar);
        }

        foreach (var (labelKey, _) in tabs)
            _bar.Items.Add(new ListBoxItem { Content = Loc.T(labelKey) });

        foreach (var (_, panel) in tabs)
        {
            panel.IsVisible = false;
            _body.Children.Add(panel);
        }

        // 내용 칸: 세로 배치면 남은 자리, 가로 배치면 아래 전부
        var content = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Children.Add(content);

        _bar.SelectionChanged += (_, _) => Show(Math.Max(0, _bar.SelectedIndex));
        _bar.SelectedIndex = 0;
    }

    /// <summary>고른 탭이 바뀌었다(인덱스).</summary>
    public event Action<int>? Changed;

    public int SelectedIndex
    {
        get => Math.Max(0, _bar.SelectedIndex);
        set => _bar.SelectedIndex = value;
    }

    private void Show(int index)
    {
        for (var i = 0; i < _body.Children.Count; i++) _body.Children[i].IsVisible = i == index;
        Changed?.Invoke(index);
    }

    private static IBrush? BrushOf(string key)
    {
        return Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value)
            ? value as IBrush
            : null;
    }
}

/// <summary>
///     값·글 쌍 목록 콤보 — 앱의 다른 화면과 같이 <see cref="ComboChoices" />(ComboBoxItem + Tag 값)로 채운다.
///     글은 키를 번역해 보이고, 저장할 값은 <see cref="ComboChoices.Selected" /> 로 읽는다.
/// </summary>
internal static class ChoiceBox
{
    public static ComboBox Create((string Value, string Label)[] choices, string? selected, bool stretch)
    {
        var box = new ComboBox { HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left };
        ComboChoices.Fill(box, choices);
        ComboChoices.Select(box, selected ?? string.Empty);
        return box;
    }
}

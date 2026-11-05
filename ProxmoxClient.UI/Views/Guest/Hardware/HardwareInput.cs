using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Views.Controls;

namespace ProxmoxClient.UI.Views.Guest.Hardware;

/// <summary>
///     하드웨어 화면의 입력 칸·단추 도우미 — WPF 의 InputFilter(정수·소수만)·FormRow(이름·칸·힌트 한 줄)·
///     InfoTip(작은 i 아이콘)·IconAssist 단추를 대신한다. 데스크톱은 WPF 와 같은 FormRow, 모바일은 세로로 쌓는다.
/// </summary>
internal static class HardwareInput
{
    /// <summary>
    ///     정수(decimals=false) 또는 소수 칸. 숫자가 아닌 글자는 입력·붙여넣기 즉시 지운다.
    /// </summary>
    /// <param name="width">칸 폭(0 이면 남은 폭을 채운다).</param>
    public static TextBox Numeric(bool decimals, double width = 0)
    {
        var box = new TextBox { MinWidth = 80, Width = width > 0 ? width : double.NaN, HorizontalAlignment = width > 0 ? HorizontalAlignment.Left : HorizontalAlignment.Stretch };
        var guard = false;
        box.TextChanged += (_, _) =>
        {
            if (guard || box.Text is not { } text) return;

            var filtered = new string(text.Where(c => char.IsAsciiDigit(c) || decimals && c == '.').ToArray());
            if (filtered == text) return;

            guard = true;
            var caret = Math.Min(box.CaretIndex, filtered.Length);
            box.Text = filtered;
            box.CaretIndex = caret;
            guard = false;
        };
        return box;
    }

    /// <summary>
    ///     칸 이름 · 입력 칸 · 힌트. 데스크톱은 WPF FormRow 처럼 이름을 왼쪽 고정폭(170px)에 두고 칸을 오른쪽에,
    ///     모바일은 412px 폭에 맞춰 이름 아래에 칸을 둔다.
    /// </summary>
    public static Control Form(string label, Control input, string? hint = null)
    {
        if (PlatformInfo.IsDesktop)
            return new FormRow { Label = label, LabelWidth = 170, Content = input, Hint = hint };

        return new Row(label, input, hint);
    }

    /// <summary>힌트 문구를 바꾼다. 비우면 줄을 감춘다.</summary>
    public static void SetHint(Control form, string? hint)
    {
        switch (form)
        {
            case FormRow row:
                row.Hint = hint;
                break;
            case Row row:
                row.Hint = hint;
                break;
        }
    }

    /// <summary>WPF 와 같은 단추: 아이콘 + 글자, 최소 폭 96(툴바). 여백은 호출하는 쪽이 정한다.</summary>
    public static Button Button(string label, string iconKey)
    {
        var button = new Button { Content = label, MinWidth = 96 };
        IconAssist.SetIcon(button, Resource(iconKey) as Geometry);
        return button;
    }

    /// <summary>WPF InfoTip(Compact) 과 같은 작은 i 아이콘 — 올리면 설명이 뜬다.</summary>
    public static Control InfoTip(string text)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Classes = { "icon" }, Width = 14, Height = 14, Data = Resource("IconInfo") as Geometry,
            Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center
        };
        var tip = new Border
        {
            Background = Brushes.Transparent, Margin = new Thickness(4, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = icon
        };
        ToolTip.SetTip(tip, text);
        return tip;
    }

    /// <summary>테마 자원(아이콘 기하·브러시) 찾기 — 없으면 null.</summary>
    public static object? Resource(string key)
    {
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value : null;
    }

    public static IBrush? Brush(string key)
    {
        return Resource(key) as IBrush;
    }

    /// <summary>정수 칸·이름이 함께 있는 한 줄(모바일용). 힌트는 이름·칸 아래 작은 글씨.</summary>
    public sealed class Row : StackPanel
    {
        private readonly TextBlock _hint = new()
        {
            Classes = { "subtle" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            TextTrimming = Avalonia.Media.TextTrimming.None, FontSize = 12, IsVisible = false
        };

        public Row(string label, Control input, string? hint = null)
        {
            Spacing = 4;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            Children.Add(new TextBlock { Text = label, Classes = { "label" } });
            Children.Add(input);
            Children.Add(_hint);
            Hint = hint;
        }

        /// <summary>힌트 문구. 비우면 줄을 감춘다.</summary>
        public string? Hint
        {
            get => _hint.Text;
            set
            {
                _hint.Text = value;
                _hint.IsVisible = !string.IsNullOrEmpty(value);
            }
        }
    }
}

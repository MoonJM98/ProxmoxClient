using Avalonia.Controls;
using Avalonia.Layout;

namespace ProxmoxClient.UI.Views.Guest.Hardware;

/// <summary>
///     하드웨어 화면의 입력 칸 도우미 — WPF 의 InputFilter(정수·소수만)와 FormRow(이름·칸·힌트 한 줄)를 대신한다.
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
    ///     칸 이름 · 입력 칸 · 힌트 한 줄(세로로 쌓는다 — 412px 폭에서도 잘리지 않는다).
    /// </summary>
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

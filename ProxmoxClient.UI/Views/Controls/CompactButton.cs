using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Controls;

/// <summary>
///     좁은 화면용 작업 버튼 규칙(모바일).
///     <list type="bullet">
///         <item>같은 줄의 버튼이 글자까지 다 넣으면 한 줄을 넘칠 때만 글자를 숨기고 아이콘만 보인다(사용자 규칙 — 버튼이 많거나
///         한 줄로 표현되지 않을 때만 생략). 한 줄에 들어가면 글자를 그대로 보인다. 폭이 바뀌면(회전·태블릿) 다시 판단한다.
///         데스크톱은 항상 WPF 와 같이 아이콘 + 글자.</item>
///         <item>길게 누르면 이름을 알림(토스트)으로 보여 주고 실행하지 않는다 — Android 의 아이콘 버튼과 같은 관례.</item>
///     </list>
///     확인·취소 같은 대화상자 버튼에는 쓰지 않는다(글자를 늘 보인다).
/// </summary>
public static class CompactButton
{

    private static readonly AttachedProperty<string?> LabelProperty =
        AvaloniaProperty.RegisterAttached<Button, string?>("CompactLabel", typeof(CompactButton));

    private static readonly AttachedProperty<bool> NarrowProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("CompactNarrow", typeof(CompactButton));

    /// <summary>
    ///     버튼 이름을 바꾼다(상황에 따라 "제거"/"분리" 등) — 좁은 화면이면 아이콘만 둔 채 이름(툴팁·길게 누르기 알림)만 바꾼다.
    ///     규칙을 걸지 않은 버튼이면 글자를 그대로 바꾼다.
    /// </summary>
    public static void SetLabel(Button button, string label)
    {
        ToolTip.SetTip(button, label);
        AutomationLabel(button, label);
        if (button.GetValue(LabelProperty) is null)
        {
            button.Content = label;
            return;
        }

        button.SetValue(LabelProperty, label);
        if (!button.GetValue(NarrowProperty)) button.Content = label;
    }
    private const int LongPressMs = 500;
    private const double MoveSlop = 12;

    /// <summary>아이콘(IconAssist.Icon)이 있는 버튼에 규칙을 건다. 실행(Click) 처리기보다 먼저 불러야 한다.</summary>
    public static void Apply(Button button, string label)
    {
        if (PlatformInfo.IsDesktop) return;

        ToolTip.SetTip(button, label);
        AutomationLabel(button, label);
        button.SetValue(LabelProperty, label);
        Panel? row = null;
        var lastKey = (Width: -1.0, Count: -1);

        // 줄의 폭이나 버튼 수가 바뀐 레이아웃마다 다시 판단한다(처음 그려질 때 포함 — 그 전에는 폭이 0)
        void OnLayoutUpdated(object? sender, EventArgs e)
        {
            if (row is null || row.Bounds.Width <= 0) return;

            var key = (row.Bounds.Width, row.Children.Count);
            if (key == lastKey) return;

            lastKey = key;
            UpdateRow(row);
        }

        button.AttachedToVisualTree += (_, _) =>
        {
            row = RowOf(button);
            lastKey = (-1, -1);
            if (row is not null) row.LayoutUpdated += OnLayoutUpdated;
        };
        button.DetachedFromVisualTree += (_, _) =>
        {
            if (row is not null) row.LayoutUpdated -= OnLayoutUpdated;
            row = null;
        };

        // 길게 누르기 → 이름 알림, 이번 누름은 실행하지 않는다
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LongPressMs) };
        var suppress = false;
        var pressedAt = default(Point);
        var pressedTick = 0L;
        var moved = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            suppress = true;
            AppServices.Dialogs.Toast(button.GetValue(LabelProperty) ?? label);
        };
        button.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            suppress = false;
            pressedAt = e.GetPosition(button);
            pressedTick = Environment.TickCount64;
            moved = false;
            timer.Start();
        }, RoutingStrategies.Tunnel, true);
        button.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (!timer.IsEnabled) return;

            var p = e.GetPosition(button) - pressedAt;
            if (Math.Abs(p.X) <= MoveSlop && Math.Abs(p.Y) <= MoveSlop) return;

            moved = true;
            timer.Stop();
        }, RoutingStrategies.Tunnel, true);
        // 손을 뗄 때도 누른 시간을 본다 — 타이머가 늦게 돌아도(바쁜 화면 등) 길게 누른 것은 실행하지 않는다
        button.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
        {
            timer.Stop();
            if (suppress || moved || Environment.TickCount64 - pressedTick < LongPressMs) return;

            suppress = true;
            AppServices.Dialogs.Toast(button.GetValue(LabelProperty) ?? label);
        }, RoutingStrategies.Tunnel, true);
        button.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => timer.Stop(), RoutingStrategies.Direct, true);
        // 실행 처리기보다 먼저 등록되므로 여기서 막으면 Click 처리기와 Command 가 실행되지 않는다
        button.Click += (_, e) =>
        {
            if (!suppress) return;

            suppress = false;
            e.Handled = true;
        };
    }

    /// <summary>버튼이 놓인 한 줄 — 위로 이어진 가로 패널(WrapPanel·가로 StackPanel) 중 가장 바깥.</summary>
    private static Panel? RowOf(Control button)
    {
        Panel? row = null;
        for (var parent = button.Parent; parent is Panel panel &&
                                         (panel is WrapPanel || panel is StackPanel { Orientation: Avalonia.Layout.Orientation.Horizontal });
             parent = panel.Parent)
            row = panel;
        return row;
    }

    /// <summary>
    ///     줄 안의 버튼이 모두 글자를 보일 때의 한 줄 폭을 어림해 줄 폭과 비교한다. 지금 아이콘만인 버튼은 글자 폭만큼 더한다 —
    ///     그래서 글자를 숨겼다 보였다 해도 같은 값이 나와 깜빡이지 않는다.
    /// </summary>
    private static void UpdateRow(Panel? row)
    {
        if (row is null || row.Bounds.Width <= 0) return;

        var available = row is WrapPanel ? row.Bounds.Width : (row.Parent as Control)?.Bounds.Width ?? row.Bounds.Width;
        var managed = row.GetLogicalDescendants().OfType<Button>()
            .Where(b => b.IsVisible && b.GetValue(LabelProperty) is not null && IconAssist.GetIcon(b) is not null).ToList();
        if (managed.Count == 0) return;

        var need = row.Children.Where(c => c.IsVisible).Sum(c => c.DesiredSize.Width);
        var wasNarrow = managed.Any(b => b.GetValue(NarrowProperty));
        foreach (var b in managed.Where(b => b.GetValue(NarrowProperty)))
            need += LabelExtra(b);
        // 경계에서 글자·아이콘을 오가지 않게 여유를 둔다 — 글자 폭 어림은 실제보다 조금 크다(아이콘만 → 글자는 넉넉할 때만)
        var narrow = wasNarrow ? need > available - 24 : need > available - 4;
        // 어림이 맞아도 실제로 줄이 꺾였으면(버튼들의 세로 위치가 다르면) 아이콘만으로
        if (!wasNarrow && !narrow)
            narrow = row.Children.Where(c => c.IsVisible && c.Bounds.Width > 0)
                .Select(c => Math.Round(c.Bounds.Y / 4)).Distinct().Count() > 1;
        foreach (var b in managed) SetNarrow(b, narrow);
    }

    /// <summary>글자를 보일 때 더 필요한 폭(글자 + 아이콘과의 간격 + 넓은 여백) — 아이콘만일 때와의 차이.</summary>
    private static double LabelExtra(Button button)
    {
        var label = button.GetValue(LabelProperty) ?? string.Empty;
        var text = new Avalonia.Media.TextFormatting.TextLayout(label,
            new Avalonia.Media.Typeface(button.FontFamily, button.FontStyle, button.FontWeight), button.FontSize, null).Width;
        var labeled = text + 16 + 6 + 24 + 2;
        var iconOnly = Math.Max(44, 16 + 20 + 2);
        return Math.Max(0, labeled - iconOnly);
    }

    private static void SetNarrow(Button button, bool narrow)
    {
        if (button.GetValue(NarrowProperty) == narrow && (narrow ? button.Content is null : button.Content is not null)) return;

        button.SetValue(NarrowProperty, narrow);
        button.Content = narrow ? null : button.GetValue(LabelProperty);
        button.MinWidth = narrow ? 44 : 0;
        button.Padding = narrow ? new Thickness(10, 6) : new Thickness(12, 6);
    }

    private static void AutomationLabel(Control control, string label)
    {
        Avalonia.Automation.AutomationProperties.SetName(control, label);
    }
}

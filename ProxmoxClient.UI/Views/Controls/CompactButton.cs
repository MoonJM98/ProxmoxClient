using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Controls;

/// <summary>
///     좁은 화면용 작업 버튼 규칙(모바일).
///     <list type="bullet">
///         <item>화면 폭이 <see cref="NarrowWidth" /> 미만(휴대폰 세로)이면 글자를 숨기고 아이콘만 보인다 — 버튼 줄이 여러 줄로 넘어가
///         표가 가려지지 않게. 넓어지면(가로 회전·태블릿) 다시 글자를 보인다. 데스크톱은 항상 WPF 와 같이 아이콘 + 글자.</item>
///         <item>길게 누르면 이름을 알림(토스트)으로 보여 주고 실행하지 않는다 — Android 의 아이콘 버튼과 같은 관례.</item>
///     </list>
///     확인·취소 같은 대화상자 버튼에는 쓰지 않는다(글자를 늘 보인다).
/// </summary>
public static class CompactButton
{
    public const double NarrowWidth = 600;
    private const int LongPressMs = 500;
    private const double MoveSlop = 12;

    /// <summary>아이콘(IconAssist.Icon)이 있는 버튼에 규칙을 건다. 실행(Click) 처리기보다 먼저 불러야 한다.</summary>
    public static void Apply(Button button, string label)
    {
        if (PlatformInfo.IsDesktop) return;

        ToolTip.SetTip(button, label);
        AutomationLabel(button, label);
        TopLevel? topLevel = null;

        void Update()
        {
            var narrow = topLevel is not null && topLevel.Bounds.Width > 0 && topLevel.Bounds.Width < NarrowWidth &&
                         IconAssist.GetIcon(button) is not null;
            button.Content = narrow ? null : label;
            button.MinWidth = narrow ? 44 : 0;
            button.Padding = narrow ? new Thickness(10, 6) : new Thickness(12, 6);
        }

        void OnTopLevelChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.BoundsProperty) Update();
        }

        button.AttachedToVisualTree += (_, _) =>
        {
            topLevel = TopLevel.GetTopLevel(button);
            if (topLevel is not null) topLevel.PropertyChanged += OnTopLevelChanged;
            Update();
        };
        button.DetachedFromVisualTree += (_, _) =>
        {
            if (topLevel is not null) topLevel.PropertyChanged -= OnTopLevelChanged;
            topLevel = null;
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
            AppServices.Dialogs.Toast(label);
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
            AppServices.Dialogs.Toast(label);
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

    private static void AutomationLabel(Control control, string label)
    {
        Avalonia.Automation.AutomationProperties.SetName(control, label);
    }
}

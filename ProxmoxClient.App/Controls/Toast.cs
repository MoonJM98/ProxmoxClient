using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ProxmoxClient.App.Controls;

/// <summary>
///     창 위에 잠깐 떴다가 서서히 사라지는 알림 — 대상 요소 위쪽 가운데에 뜬다. Popup(별도 창)이라 네이티브 창(터미널)
///     위에도 보이고, 포커스를 가져가지 않는다(콘솔 키 입력을 방해하지 않게). ✕ 로 바로 닫고, 마우스를 올려 두면
///     사라지지 않는다. 창을 옮기면 따라가고, 창이 비활성·최소화되면 숨는다. 새 알림은 앞의 것을 바꾼다.
/// </summary>
public sealed class Toast
{
    private const double TopMargin = 14;
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private static readonly TimeSpan MinVisible = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxVisible = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PerChar = TimeSpan.FromMilliseconds(45);
    private static readonly Duration FadeIn = TimeSpan.FromMilliseconds(150);
    private static readonly Duration FadeOut = TimeSpan.FromMilliseconds(450);

    private readonly Border _border;
    private readonly Popup _popup;
    private readonly TextBlock _text;
    private readonly DispatcherTimer _timer = new();

    public Toast(FrameworkElement target)
    {
        _text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, MaxWidth = 560, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)target.FindResource("BrushText")
        };
        // 닫기 — 누르는 것만, 키보드 포커스는 가져가지 않는다
        var close = new Button
        {
            Style = (Style)target.FindResource("RoundCloseButton"), Focusable = false,
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
        };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        _border = new Border
        {
            Child = new DockPanel { Children = { close, _text } }, Padding = new Thickness(14, 8, 10, 8),
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)target.FindResource("BrushBorder"),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x25, 0x25, 0x26))
        };
        // 올려 두는 동안은 사라지지 않는다(읽는 중)
        _border.MouseEnter += (_, _) =>
        {
            _timer.Stop();
            _border.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, FadeIn));
        };
        _border.MouseLeave += (_, _) =>
        {
            if (_popup!.IsOpen) _timer.Start();
        };
        _popup = new Popup
        {
            Child = _border, PlacementTarget = target, Placement = PlacementMode.Custom, AllowsTransparency = true,
            StaysOpen = true, Focusable = false,
            CustomPopupPlacementCallback = (popup, box, _) =>
                [new CustomPopupPlacement(new Point((box.Width - popup.Width) / 2, TopMargin), PopupPrimaryAxis.None)]
        };
        _popup.Opened += (_, _) => MakeNoActivate();
        _timer.Tick += (_, _) => Hide();

        target.Loaded += (_, _) => Attach(Window.GetWindow(target));
    }

    /// <summary>알림을 띄운다 — 글이 길면 조금 더 오래 보인다.</summary>
    public void Show(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        _text.Text = text;
        _timer.Stop();
        var visible = Math.Clamp(PerChar.Ticks * text.Length, MinVisible.Ticks, MaxVisible.Ticks);
        _timer.Interval = TimeSpan.FromTicks(visible);
        if (Window.GetWindow(_popup.PlacementTarget) is { IsActive: true, WindowState: not WindowState.Minimized })
        {
            _border.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, FadeIn));
            _popup.IsOpen = true;
            Reposition();
        }

        _timer.Start();
    }

    /// <summary>서서히 사라진다.</summary>
    private void Hide()
    {
        _timer.Stop();
        if (!_popup.IsOpen) return;

        var fade = new DoubleAnimation(0, FadeOut);
        fade.Completed += (_, _) =>
        {
            if (_border.Opacity == 0) _popup.IsOpen = false;
        };
        _border.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void Attach(Window? window)
    {
        if (window is null) return;

        window.LocationChanged += (_, _) => Reposition();
        window.SizeChanged += (_, _) => Reposition();
        window.Deactivated += (_, _) => Close();
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized) Close();
        };
        window.Closed += (_, _) => Close();
    }

    private void Close()
    {
        _timer.Stop();
        _popup.IsOpen = false;
    }

    /// <summary>Popup 은 창을 따라 움직이지 않으므로 위치를 다시 계산하게 한다.</summary>
    private void Reposition()
    {
        if (!_popup.IsOpen) return;

        _popup.HorizontalOffset += 0.01;
        _popup.HorizontalOffset -= 0.01;
    }

    /// <summary>알림 창을 눌러도 활성화되지 않게(콘솔 창이 키 입력을 계속 받게).</summary>
    private void MakeNoActivate()
    {
        if (PresentationSource.FromVisual(_border) is not HwndSource source) return;

        var style = GetWindowLong(source.Handle, GwlExStyle);
        SetWindowLong(source.Handle, GwlExStyle, style | WsExNoActivate);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ProxmoxClient.UI.Views.Controls;

/// <summary>
///     화면 위에 떠 있는 둥근 단추(버블) — 누르면 <see cref="Clicked" />, 끌면 화면 안에서 옮겨진다.
///     원격 화면 위에 겹쳐 두므로 자기에게 온 터치·마우스는 모두 처리해 아래 화면으로 넘기지 않는다.
/// </summary>
public sealed class FloatingBubble : Border
{
    public const double Size = 48;
    private const double DragSlop = 8;
    private const double Edge = 8;

    private readonly TranslateTransform _offset = new();
    private Point? _pressAt;
    private Vector _pressOffset;
    private bool _dragging;
    private Control? _host;

    public FloatingBubble(string name)
    {
        Width = Height = Size;
        CornerRadius = new CornerRadius(Size / 2);
        Background = new SolidColorBrush(Color.Parse("#99202020"));
        BorderBrush = new SolidColorBrush(Color.Parse("#80FFFFFF"));
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, TopInset, Edge, 0);
        RenderTransform = _offset;
        Cursor = new Cursor(StandardCursorType.Hand);
        AutomationProperties.SetName(this, name);
        ToolTip.SetTip(this, name);
    }

    /// <summary>처음 자리의 위쪽 여백 — 메뉴를 열었을 때 위쪽 막대와 겹치지 않게 그 아래에 둔다.</summary>
    public const double TopInset = 64;

    public event Action? Clicked;

    /// <summary>그림(아이콘) 바꾸기 — 열림/닫힘 표시.</summary>
    public void SetIcon(string resourceKey)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Classes = { "icon" }, Stroke = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (Application.Current?.TryGetResource(resourceKey, Application.Current.ActualThemeVariant, out var data) == true)
            icon.Data = data as Geometry;
        Child = icon;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _host = Parent as Control;
        if (_host is not null) _host.SizeChanged += OnHostSizeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_host is not null) _host.SizeChanged -= OnHostSizeChanged;
        _host = null;
        base.OnDetachedFromVisualTree(e);
    }

    // 화면이 돌거나 크기가 바뀌면 버블이 밖으로 나가지 않게 다시 맞춘다
    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        SetOffset(new Vector(_offset.X, _offset.Y));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_host is null) return;

        _pressAt = e.GetPosition(_host);
        _pressOffset = new Vector(_offset.X, _offset.Y);
        _dragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressAt is not { } start || _host is null) return;

        var delta = e.GetPosition(_host) - start;
        if (!_dragging && Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) > DragSlop) _dragging = true;
        if (_dragging) SetOffset(_pressOffset + delta);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressAt is null) return;

        var click = !_dragging;
        _pressAt = null;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (click) Clicked?.Invoke();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressAt = null;
        _dragging = false;
    }

    /// <summary>처음 자리(오른쪽 위)에서 옮긴 거리 — 화면 가장자리 안으로 제한한다.</summary>
    private void SetOffset(Vector offset)
    {
        if (_host is null) return;

        var width = _host.Bounds.Width;
        var height = _host.Bounds.Height;
        // 처음 자리: 오른쪽 끝에서 Edge, 위에서 TopInset. 왼쪽으로는 화면 왼쪽 Edge 까지, 아래로는 바닥 Edge 까지
        var minX = -Math.Max(0, width - Size - 2 * Edge);
        var minY = -Math.Max(0, TopInset - Edge);
        var maxY = Math.Max(minY, height - Size - Edge - TopInset);
        _offset.X = Math.Clamp(offset.X, minX, 0);
        _offset.Y = Math.Clamp(offset.Y, minY, maxY);
    }
}

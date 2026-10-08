using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ProxmoxClient.App.Controls;

/// <summary>
///     주소 표시줄의 경로 조각을 가로로 늘어놓는다 — 탐색기처럼 자리가 모자라면 앞(상위) 폴더부터
///     접고 맨 앞에 "…" 버튼(첫 자식)을 보인다. 나머지 자식은 [폴더, &gt;] 두 개씩 짝으로 들어온다.
///     접힌 짝 수는 <see cref="CollapsedPairs" /> 로 알린다(… 메뉴에 넣을 폴더). 마지막 폴더는 접지 않는다.
///     접힌 조각은 화면 밖에 두고 Tab 으로도 가지 않게 한다.
/// </summary>
public sealed class BreadcrumbPanel : Panel
{
    private static readonly Rect Hidden = new(-10000, 0, 0, 0);

    public BreadcrumbPanel()
    {
        ClipToBounds = true;
    }

    /// <summary>자리가 모자라 접힌 앞쪽 [폴더, &gt;] 짝의 수.</summary>
    public int CollapsedPairs { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var infinite = new Size(double.PositiveInfinity, availableSize.Height);
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(infinite);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        CollapsedPairs = CountCollapsed(availableSize.Width);
        return new Size(double.IsInfinity(availableSize.Width) ? TotalWidth(0, false) : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        CollapsedPairs = CountCollapsed(finalSize.Width);
        double x = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var visible = i == 0 ? CollapsedPairs > 0 : (i - 1) / 2 >= CollapsedPairs;
            KeyboardNavigation.SetIsTabStop(child, visible);
            if (!visible || child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(Hidden);
                continue;
            }

            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
        }

        return finalSize;
    }

    /// <summary>폭 안에 들어갈 때까지 앞쪽 짝을 접는다 — 마지막 폴더 짝은 남긴다.</summary>
    private int CountCollapsed(double available)
    {
        var pairs = (InternalChildren.Count - 1) / 2;
        if (double.IsInfinity(available) || TotalWidth(0, false) <= available) return 0;

        var collapsed = 1;
        while (collapsed < pairs - 1 && TotalWidth(collapsed, true) > available) collapsed++;
        return Math.Min(collapsed, Math.Max(pairs - 1, 0));
    }

    /// <summary>앞 <paramref name="skip" /> 짝을 뺀 너비(… 포함 여부 선택).</summary>
    private double TotalWidth(int skip, bool withOverflow)
    {
        var width = withOverflow && InternalChildren.Count > 0 ? InternalChildren[0].DesiredSize.Width : 0;
        for (var i = 1 + skip * 2; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (child.Visibility != Visibility.Collapsed) width += child.DesiredSize.Width;
        }

        return width;
    }
}

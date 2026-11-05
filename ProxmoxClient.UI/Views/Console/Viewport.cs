using Avalonia;

namespace ProxmoxClient.UI.Views.Console;

/// <summary>
///     원격 화면을 보기 영역에 놓는 방법 — 화면 맞춤 배율 × 확대 배율, 그리고 이동(원격 화면 왼쪽 위가 보기 영역의 어디에
///     오는지). 확대·이동은 늘 원격 화면이 보기 영역을 벗어나 빈 곳이 생기지 않게 맞춘다.
/// </summary>
public sealed class Viewport
{
    public const double MaxZoom = 5;

    public Size View { get; private set; }

    public Size Remote { get; private set; }

    /// <summary>화면 맞춤 배율에 곱하는 확대 배율(1 = 전체가 보임).</summary>
    public double Zoom { get; private set; } = 1;

    /// <summary>보기 픽셀 / 원격 픽셀.</summary>
    public double Scale => FitScale * Zoom;

    public double FitScale => Remote.Width <= 0 || Remote.Height <= 0 || View.Width <= 0 || View.Height <= 0
        ? 1
        : Math.Min(View.Width / Remote.Width, View.Height / Remote.Height);

    /// <summary>원격 화면 (0,0) 이 놓이는 보기 좌표.</summary>
    public Point Offset { get; private set; }

    public event Action? Changed;

    public void SetSizes(Size view, Size remote)
    {
        var refit = remote != Remote;
        View = view;
        Remote = remote;
        if (refit) Zoom = 1;
        Clamp();
    }

    public Point ToRemote(Point view)
    {
        var x = (view.X - Offset.X) / Scale;
        var y = (view.Y - Offset.Y) / Scale;
        return new Point(Math.Clamp(x, 0, Math.Max(0, Remote.Width - 1)), Math.Clamp(y, 0, Math.Max(0, Remote.Height - 1)));
    }

    public Point ToView(Point remote)
    {
        return new Point(Offset.X + remote.X * Scale, Offset.Y + remote.Y * Scale);
    }

    /// <summary>보기 좌표 <paramref name="center" /> 를 고정점으로 확대·축소.</summary>
    public void ZoomAt(double factor, Point center)
    {
        var anchor = new Point((center.X - Offset.X) / Scale, (center.Y - Offset.Y) / Scale);
        Zoom = Math.Clamp(Zoom * factor, 1, MaxZoom);
        Offset = new Point(center.X - anchor.X * Scale, center.Y - anchor.Y * Scale);
        Clamp();
    }

    public void PanBy(Vector delta)
    {
        Offset += delta;
        Clamp();
    }

    public void Fit()
    {
        Zoom = 1;
        Clamp();
    }

    /// <summary>원격 좌표가 보기 영역 안(가장자리 여유 포함)에 오도록 이동 — 마우스 모드에서 커서를 따라간다.</summary>
    public void EnsureVisible(Point remote, double margin = 40)
    {
        var p = ToView(remote);
        var dx = p.X < margin ? margin - p.X : p.X > View.Width - margin ? View.Width - margin - p.X : 0;
        var dy = p.Y < margin ? margin - p.Y : p.Y > View.Height - margin ? View.Height - margin - p.Y : 0;
        if (dx != 0 || dy != 0) PanBy(new Vector(dx, dy));
    }

    private void Clamp()
    {
        var w = Remote.Width * Scale;
        var h = Remote.Height * Scale;
        // 원격 화면이 보기보다 작으면 가운데, 크면 빈 곳이 생기지 않게
        var x = w <= View.Width ? (View.Width - w) / 2 : Math.Clamp(Offset.X, View.Width - w, 0);
        var y = h <= View.Height ? (View.Height - h) / 2 : Math.Clamp(Offset.Y, View.Height - h, 0);
        Offset = new Point(x, y);
        Changed?.Invoke();
    }
}

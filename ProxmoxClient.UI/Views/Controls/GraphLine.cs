using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace ProxmoxClient.UI.Views.Controls;

/// <summary>
///     RRD 사용량 그래프 — WPF GraphLine 과 같은 속성(Series · XLabels · Timestamps).
///     왼쪽·오른쪽 두 축, 격자, 범례, 결측 구간은 선을 끊는다. 마우스를 올리거나(데스크톱) 누르면(모바일)
///     그 시점의 값을 보여 준다.
/// </summary>
public sealed class GraphLine : Control
{
    public static readonly StyledProperty<IReadOnlyList<GraphSeries>?> SeriesProperty =
        AvaloniaProperty.Register<GraphLine, IReadOnlyList<GraphSeries>?>(nameof(Series));

    public static readonly StyledProperty<IReadOnlyList<string>?> XLabelsProperty =
        AvaloniaProperty.Register<GraphLine, IReadOnlyList<string>?>(nameof(XLabels));

    public static readonly StyledProperty<IReadOnlyList<DateTime?>?> TimestampsProperty =
        AvaloniaProperty.Register<GraphLine, IReadOnlyList<DateTime?>?>(nameof(Timestamps));

    private const int GridDivisions = 4;
    private const double FontSize = 11;
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#8A8A8A"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#338A8A8A")), 1);
    private static readonly IPen AxisPen = new Pen(new SolidColorBrush(Color.Parse("#668A8A8A")), 1);
    private int? _hover;
    private Rect _plot;

    static GraphLine()
    {
        AffectsRender<GraphLine>(SeriesProperty, XLabelsProperty, TimestampsProperty);
    }

    public GraphLine()
    {
        MinHeight = 160;
        ClipToBounds = true;
    }

    public IReadOnlyList<GraphSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public IReadOnlyList<string>? XLabels
    {
        get => GetValue(XLabelsProperty);
        set => SetValue(XLabelsProperty, value);
    }

    public IReadOnlyList<DateTime?>? Timestamps
    {
        get => GetValue(TimestampsProperty);
        set => SetValue(TimestampsProperty, value);
    }

    public override void Render(DrawingContext dc)
    {
        var bounds = Bounds;
        dc.FillRectangle(Brushes.Transparent, new Rect(bounds.Size));
        var series = (Series ?? []).Where(s => s.HasData).ToList();
        if (series.Count == 0 || bounds.Width < 80 || bounds.Height < 60) return;

        var left = series.Where(s => s.Axis == GraphAxis.Left).ToList();
        var right = series.Where(s => s.Axis == GraphAxis.Right).ToList();
        var leftMax = AxisMax(left);
        var rightMax = AxisMax(right);
        var leftUnit = left.FirstOrDefault()?.Unit ?? GraphValueUnit.Number;
        var rightUnit = right.FirstOrDefault()?.Unit ?? GraphValueUnit.Number;

        const double legendHeight = 20;
        const double xLabelHeight = 16;
        var leftWidth = Enumerable.Range(0, GridDivisions + 1)
            .Max(i => Text(FormatValue(leftMax * i / GridDivisions, leftUnit)).Width) + 6;
        var rightWidth = right.Count == 0
            ? 8
            : Enumerable.Range(0, GridDivisions + 1).Max(i => Text(FormatValue(rightMax * i / GridDivisions, rightUnit)).Width) + 6;
        _plot = new Rect(leftWidth, legendHeight + 4, Math.Max(10, bounds.Width - leftWidth - rightWidth),
            Math.Max(10, bounds.Height - legendHeight - xLabelHeight - 8));

        for (var i = 0; i <= GridDivisions; i++)
        {
            var y = _plot.Top + _plot.Height * i / GridDivisions;
            dc.DrawLine(i == GridDivisions ? AxisPen : GridPen, new Point(_plot.Left, y), new Point(_plot.Right, y));
            var value = leftMax * (GridDivisions - i) / GridDivisions;
            var label = Text(FormatValue(value, leftUnit));
            dc.DrawText(label, new Point(_plot.Left - label.Width - 4, y - label.Height / 2));
            if (right.Count > 0)
            {
                var r = Text(FormatValue(rightMax * (GridDivisions - i) / GridDivisions, rightUnit));
                dc.DrawText(r, new Point(_plot.Right + 4, y - r.Height / 2));
            }
        }

        // 범례
        var x = _plot.Left;
        foreach (var s in series)
        {
            dc.FillRectangle(new SolidColorBrush(s.Color), new Rect(x, 6, 10, 10), 2);
            var name = Text(s.Name);
            dc.DrawText(name, new Point(x + 14, 4));
            x += 14 + name.Width + 14;
        }

        // 가로축 이름(처음·가운데·끝)
        if (XLabels is { Count: > 0 } labels)
            foreach (var index in new[] { 0, labels.Count / 2, labels.Count - 1 }.Distinct())
            {
                var t = Text(labels[index]);
                var px = PointX(index, labels.Count) - t.Width / 2;
                px = Math.Clamp(px, _plot.Left, _plot.Right - t.Width);
                dc.DrawText(t, new Point(px, _plot.Bottom + 3));
            }

        foreach (var s in series) DrawSeries(dc, s, s.Axis == GraphAxis.Left ? leftMax : rightMax);

        if (_hover is { } hover) DrawHover(dc, series, hover, leftMax, rightMax);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetHover(e.GetPosition(this));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        SetHover(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    private void SetHover(Point point)
    {
        var count = Series?.FirstOrDefault()?.Values.Count ?? 0;
        if (count < 2 || !_plot.Contains(point))
        {
            _hover = null;
        }
        else
        {
            _hover = (int)Math.Round((point.X - _plot.Left) / _plot.Width * (count - 1));
        }

        InvalidateVisual();
    }

    private void DrawHover(DrawingContext dc, IReadOnlyList<GraphSeries> series, int index, double leftMax,
        double rightMax)
    {
        var count = series[0].Values.Count;
        if (index < 0 || index >= count) return;

        var px = PointX(index, count);
        dc.DrawLine(AxisPen, new Point(px, _plot.Top), new Point(px, _plot.Bottom));
        var lines = new List<string>();
        if (Timestamps is { } stamps && index < stamps.Count && stamps[index] is { } time)
            lines.Add(time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        else if (XLabels is { } labels && index < labels.Count)
            lines.Add(labels[index]);
        foreach (var s in series)
            if (index < s.Values.Count && s.Values[index] is { } v)
            {
                lines.Add($"{s.Name}: {FormatValue(v, s.Unit)}");
                var max = s.Axis == GraphAxis.Left ? leftMax : rightMax;
                dc.DrawEllipse(new SolidColorBrush(s.Color), null, new Point(px, PointY(v, max)), 3, 3);
            }

        var text = Text(string.Join("\n", lines), Brushes.White);
        var box = new Rect(Math.Min(px + 8, _plot.Right - text.Width - 12), _plot.Top + 4, text.Width + 12,
            text.Height + 8);
        dc.FillRectangle(new SolidColorBrush(Color.Parse("#E6303030")), box, 6);
        dc.DrawText(text, new Point(box.X + 6, box.Y + 4));
    }

    private void DrawSeries(DrawingContext dc, GraphSeries series, double max)
    {
        var count = series.Values.Count;
        if (count < 2) return;

        var pen = new Pen(new SolidColorBrush(series.Color), 1.6, lineJoin: PenLineJoin.Round);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var open = false;
            for (var i = 0; i < count; i++)
            {
                if (series.Values[i] is not { } value)
                {
                    if (open) context.EndFigure(false);
                    open = false;
                    continue;
                }

                var point = new Point(PointX(i, count), PointY(value, max));
                if (!open)
                {
                    context.BeginFigure(point, false);
                    open = true;
                }
                else
                {
                    context.LineTo(point);
                }
            }

            if (open) context.EndFigure(false);
        }

        dc.DrawGeometry(null, pen, geometry);
    }

    private double PointX(int index, int count)
    {
        return _plot.Left + _plot.Width * index / Math.Max(1, count - 1);
    }

    private double PointY(double value, double max)
    {
        return _plot.Bottom - _plot.Height * Math.Clamp(value / max, 0, 1);
    }

    private static double AxisMax(IReadOnlyList<GraphSeries> series)
    {
        if (series.Count == 0) return 1;
        if (series.All(s => s.Unit == GraphValueUnit.Percent)) return 100;

        var max = series.SelectMany(s => s.Values).Where(v => v is not null).Select(v => v!.Value)
            .DefaultIfEmpty(0).Max();
        if (max <= 0) return 1;

        // 보기 좋은 눈금(1·2·5 × 10^n)
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var step in new[] { 1.0, 2, 5, 10 })
            if (max <= step * magnitude)
                return step * magnitude;
        return 10 * magnitude;
    }

    private static string FormatValue(double value, GraphValueUnit unit)
    {
        return unit switch
        {
            GraphValueUnit.Percent => value.ToString("0", CultureInfo.CurrentCulture) + "%",
            GraphValueUnit.BytesPerSecond => ByteFormatter.Format(value) + "/s",
            _ => value >= 100 ? value.ToString("0", CultureInfo.CurrentCulture)
                : value.ToString("0.##", CultureInfo.CurrentCulture)
        };
    }

    private static FormattedText Text(string text, IBrush? brush = null)
    {
        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default,
            FontSize, brush ?? TextBrush);
    }
}

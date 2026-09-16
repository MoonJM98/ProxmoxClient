using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ProxmoxClient.App.Controls;

/// <summary>
///     rrddata 통합 시계열 차트 — 여러 시리즈를 색으로 구분하고 왼쪽(%)·오른쪽(초당 바이트) 이중 Y 축을 그린다.
///     범례(색상 띠)를 클릭하면 시리즈를 흐리게(약 18% 불투명도) 처리하거나 되돌리며, 마우스 오버 시 강조된 시리즈 값을 팝업으로 보여준다.
///     바이트 축 최댓값은 1024 단위의 짝수 값(2 MB, 800 KB …)으로 올림해 중간 눈금도 딱 떨어진다.
///     렌더 비용:
///     - 본 차트(격자·축·범례·선)는 데이터·크기·강조 상태가 바뀔 때만 다시 그린다.
///     - 마우스 오버 표시(점선·점)는 별도 <see cref="DrawingVisual" /> 에만 그려, 커서 이동 때 차트 전체를 재생성하지 않는다.
///     - 표시 대상 목록·시점별 값 존재 여부는 Series/강조 변경 시 한 번 계산해 렌더·마우스 이동 경로에 LINQ 할당이 없다.
///     - 라벨 FormattedText 는 DPI 별로 캐시하고, 팝업 행은 시리즈 구성이 바뀔 때만 만든다.
/// </summary>
public sealed class GraphLine : FrameworkElement
{
    private const double LabelFontSize = 10;
    private const double MinLeftPad = 24;
    private const double AxisLabelGap = 6;
    private const double RightPad = 8;
    private const double LegendGap = 6;
    private const double LegendBandWidth = 16;
    private const double LegendBandHeight = 6;
    private const double LegendTextGap = 5;
    private const double LegendItemGap = 16;
    private const double XLabelGap = 2;
    private const double PopupCursorOffset = 14;
    private const double HoverDotRadius = 3.5;
    private const double LineThickness = 1.6;
    private const double PercentAxisMax = 100;
    private const int GridDivisions = 4;
    private const int MaxTickLabels = 3;
    private const int MaxCachedLabels = 128;

    /// <summary>범례 클릭으로 흐리게 처리한 시리즈의 불투명도(약 18%).</summary>
    private const byte DimmedAlpha = 0x2E;

    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly Brush TransparentHitBrush = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB0)));
    private static readonly Brush DimmedLabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5A, 0x5E, 0x6A)));

    private static readonly Pen GridPen =
        Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x34, 0x3C))), 1));

    private static readonly Pen AxisPen =
        Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x55, 0x57, 0x62))), 1));

    private static readonly Pen HoverPen = Freeze(
        new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0xC8, 0xCC, 0xD8))), 1)
        {
            DashStyle = DashStyles.Dash
        });

    private static readonly Pen HoverDotPen = Freeze(new Pen(Brushes.White, 1.5));

    /// <summary>1024 단위 축 최댓값 후보 — 절반(중간 눈금)도 정수. 1024 = 다음 단위의 1(예: 1 MB, 중간 512 KB).</summary>
    private static readonly double[] NiceBinarySteps =
        [2, 4, 8, 10, 16, 20, 32, 40, 50, 64, 80, 100, 128, 160, 200, 256, 320, 400, 500, 512, 640, 800, 1000, 1024];

    /// <summary>일반 수치 축 최댓값 배수(×10^n).</summary>
    private static readonly double[] NiceDecimalFactors = [2, 4, 6, 8, 10];

    /// <summary>시리즈 색상별 브러시·펜 캐시(UI 스레드 전용) — 렌더마다 새로 만들지 않는다.</summary>
    private static readonly Dictionary<Color, SeriesPaint> PaintCache = [];

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<GraphSeries>), typeof(GraphLine),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty XLabelsProperty = DependencyProperty.Register(
        nameof(XLabels), typeof(IReadOnlyList<string>), typeof(GraphLine),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TimestampsProperty = DependencyProperty.Register(
        nameof(Timestamps), typeof(IReadOnlyList<DateTime?>), typeof(GraphLine),
        new FrameworkPropertyMetadata(null, (d, _) => ((GraphLine)d).HideHover()));

    private readonly HashSet<string> _dimmedSeries = new(StringComparer.Ordinal);
    private readonly List<GraphSeries> _emphasized = [];

    private readonly DrawingVisual _hoverVisual = new();
    private readonly Dictionary<(string Text, bool Dimmed), FormattedText> _labelCache = [];
    private readonly List<(Rect Bounds, string Name)> _legendHits = [];

    // Series 변경·강조 토글 때만 다시 계산하는 파생 상태
    private readonly List<GraphSeries> _legendSeries = [];

    private readonly Popup _popup;
    private readonly StackPanel _popupContent = new();
    private readonly List<PopupRow> _popupRows = [];

    private readonly TextBlock _popupTime = new()
    {
        Foreground = LabelBrush,
        FontSize = 11,
        Margin = new Thickness(0, 0, 0, 4)
    };

    // 곡선 계산용 재사용 버퍼(렌더마다 새로 할당하지 않는다)
    private readonly List<Point> _runPoints = [];
    private bool[] _hasValueAt = [];
    private int _hoverIndex = -1;
    private double _labelCacheDpi;
    private double _leftMax = PercentAxisMax;

    private Rect _plot = Rect.Empty;
    private int _pointCount;
    private double _rightMax = 1;
    private double[] _secants = [];
    private double[] _slopes = [];

    public GraphLine()
    {
        _popupContent.Children.Add(_popupTime);
        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Child = new Border
            {
                Background = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0x25, 0x25, 0x26))),
                BorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46))),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 6, 9, 6),
                Child = _popupContent
            }
        };

        AddVisualChild(_hoverVisual);
        Unloaded += (_, _) => HideHover();
    }

    /// <summary>표시할 시리즈 목록(모든 시리즈는 같은 시점 배열을 공유).</summary>
    public IReadOnlyList<GraphSeries>? Series
    {
        get => (IReadOnlyList<GraphSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    /// <summary>X 눈금 라벨(좌·중·우 3개).</summary>
    public IReadOnlyList<string>? XLabels
    {
        get => (IReadOnlyList<string>?)GetValue(XLabelsProperty);
        set => SetValue(XLabelsProperty, value);
    }

    /// <summary>각 지점의 시각 — 마우스 오버 팝업에 표시.</summary>
    public IReadOnlyList<DateTime?>? Timestamps
    {
        get => (IReadOnlyList<DateTime?>?)GetValue(TimestampsProperty);
        set => SetValue(TimestampsProperty, value);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index)
    {
        return index == 0 ? _hoverVisual : throw new ArgumentOutOfRangeException(nameof(index));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        // 빈 영역에서도 마우스 이벤트를 받도록 투명(알파 1) 배경
        dc.DrawRectangle(TransparentHitBrush, null, new Rect(0, 0, width, height));

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var hasRightAxis = HasAxis(GraphAxis.Right);

        // 흐리게 처리한 시리즈도 축 계산에 포함 — 토글할 때 축이 튀지 않도록
        _leftMax = AxisMax(GraphAxis.Left);
        _rightMax = AxisMax(GraphAxis.Right);
        var leftLabels = AxisLabels(GraphAxis.Left, _leftMax);
        var rightLabels = hasRightAxis ? AxisLabels(GraphAxis.Right, _rightMax) : null;

        _plot = ComputePlotArea(width, height, dpi, leftLabels, rightLabels, _legendSeries.Count > 0);
        if (_plot.IsEmpty)
        {
            _legendHits.Clear();
            RenderHover();
            return;
        }

        for (var i = 0; i <= GridDivisions; i++)
        {
            var y = _plot.Top + _plot.Height * i / GridDivisions;
            dc.DrawLine(i == GridDivisions ? AxisPen : GridPen, new Point(_plot.Left, y), new Point(_plot.Right, y));
        }

        dc.DrawLine(AxisPen, _plot.TopLeft, _plot.BottomLeft);
        if (hasRightAxis) dc.DrawLine(AxisPen, _plot.TopRight, _plot.BottomRight);

        DrawLegend(dc, dpi);
        DrawAxisLabels(dc, leftLabels, GraphAxis.Left, dpi);
        if (rightLabels is not null) DrawAxisLabels(dc, rightLabels, GraphAxis.Right, dpi);

        DrawXLabels(dc, dpi);

        // 흐린 시리즈를 먼저 그려 강조 시리즈가 위에 오도록
        foreach (var series in _legendSeries)
            if (!IsEmphasized(series))
                DrawSeries(dc, series, PaintFor(series.Color).DimLine);

        foreach (var series in _emphasized) DrawSeries(dc, series, PaintFor(series.Color).Line);

        RenderHover(); // 플롯 영역·축이 바뀌었을 수 있으므로 오버레이도 맞춘다
    }

    private void DrawSeries(DrawingContext dc, GraphSeries series, Pen pen)
    {
        var values = series.Values;
        var count = values.Count;
        if (count < 2) return;

        var max = MaxFor(series);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            _runPoints.Clear();
            for (var i = 0; i < count; i++)
            {
                if (values[i] is { } value)
                {
                    _runPoints.Add(ToPoint(i, value, count, max));
                    continue;
                }

                DrawRun(context); // 결측 구간은 선을 끊는다
            }

            DrawRun(context);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>
    ///     모아 둔 연속 구간을 부드러운 곡선으로 그린다(그린 뒤 비움).
    ///     단조 3차 보간(Fritsch–Carlson)이라 점 사이에서 값이 데이터 범위를 넘어 출렁이지 않는다
    ///     — 일반 스플라인은 0% 아래나 최댓값 위로 튀어 없는 값을 보여 줄 수 있다.
    /// </summary>
    private void DrawRun(StreamGeometryContext context)
    {
        var n = _runPoints.Count;
        if (n == 0) return;

        context.BeginFigure(_runPoints[0], false, false);
        if (n == 1)
        {
            _runPoints.Clear(); // 값 하나뿐인 구간은 선이 없다
            return;
        }

        if (_slopes.Length < n)
        {
            _slopes = new double[n];
            _secants = new double[n];
        }

        for (var i = 0; i < n - 1; i++)
        {
            var dx = _runPoints[i + 1].X - _runPoints[i].X;
            _secants[i] = dx > 0 ? (_runPoints[i + 1].Y - _runPoints[i].Y) / dx : 0;
        }

        _slopes[0] = _secants[0];
        _slopes[n - 1] = _secants[n - 2];
        for (var i = 1; i < n - 1; i++) _slopes[i] = (_secants[i - 1] + _secants[i]) / 2;

        // 단조 보정: 평평한 구간은 기울기 0, 그 외에는 원 데이터를 넘지 않도록 기울기를 줄인다
        for (var i = 0; i < n - 1; i++)
        {
            if (_secants[i] == 0)
            {
                _slopes[i] = 0;
                _slopes[i + 1] = 0;
                continue;
            }

            var alpha = _slopes[i] / _secants[i];
            var beta = _slopes[i + 1] / _secants[i];
            var magnitude = alpha * alpha + beta * beta;
            if (magnitude > 9)
            {
                var scale = 3 / Math.Sqrt(magnitude);
                _slopes[i] = scale * alpha * _secants[i];
                _slopes[i + 1] = scale * beta * _secants[i];
            }
        }

        for (var i = 0; i < n - 1; i++)
        {
            var start = _runPoints[i];
            var end = _runPoints[i + 1];
            var third = (end.X - start.X) / 3;
            context.BezierTo(
                new Point(start.X + third, start.Y + _slopes[i] * third),
                new Point(end.X - third, end.Y - _slopes[i + 1] * third),
                end,
                true,
                true);
        }

        _runPoints.Clear();
    }

    /// <summary>마우스 오버 점선·점만 별도 비주얼에 다시 그린다(차트 본체는 그대로).</summary>
    private void RenderHover()
    {
        using var dc = _hoverVisual.RenderOpen();
        if (_hoverIndex < 0 || _hoverIndex >= _pointCount || _pointCount < 2 ||
            _plot.IsEmpty) return; // 빈 내용으로 열고 닫아 이전 표시를 지운다

        var x = _plot.Left + _plot.Width * _hoverIndex / (_pointCount - 1);
        dc.DrawLine(HoverPen, new Point(x, _plot.Top), new Point(x, _plot.Bottom));
        foreach (var series in _emphasized)
        {
            var values = series.Values;
            if (_hoverIndex < values.Count && values[_hoverIndex] is { } value)
                dc.DrawEllipse(PaintFor(series.Color).Fill, HoverDotPen,
                    ToPoint(_hoverIndex, value, values.Count, MaxFor(series)), HoverDotRadius, HoverDotRadius);
        }
    }

    /// <summary>범례: 시리즈 색상 띠 + 이름. 흐린 시리즈는 빈 띠·흐린 글자로 표시하고 클릭 영역을 기록한다.</summary>
    private void DrawLegend(DrawingContext dc, double dpi)
    {
        _legendHits.Clear();
        var rowHeight = Label("0", false, dpi).Height;
        var x = _plot.Left;
        foreach (var series in _legendSeries)
        {
            var dimmed = !IsEmphasized(series);
            var paint = PaintFor(series.Color);
            var band = new Rect(x, (rowHeight - LegendBandHeight) / 2, LegendBandWidth, LegendBandHeight);
            if (dimmed)
                dc.DrawRoundedRectangle(null, paint.Line, band, 2, 2);
            else
                dc.DrawRoundedRectangle(paint.Fill, null, band, 2, 2);

            var text = Label(series.Name, dimmed, dpi);
            dc.DrawText(text, new Point(band.Right + LegendTextGap, 0));

            var itemWidth = LegendBandWidth + LegendTextGap + text.WidthIncludingTrailingWhitespace;
            _legendHits.Add((new Rect(x, 0, itemWidth, rowHeight), series.Name));
            x += itemWidth + LegendItemGap;
        }
    }

    private void DrawAxisLabels(DrawingContext dc, string[] labels, GraphAxis axis, double dpi)
    {
        for (var i = 0; i < labels.Length && i < MaxTickLabels; i++)
        {
            var text = Label(labels[i], false, dpi);
            var lineY = _plot.Bottom - _plot.Height * i / (MaxTickLabels - 1);
            var x = axis == GraphAxis.Left
                ? _plot.Left - text.WidthIncludingTrailingWhitespace - AxisLabelGap / 2
                : _plot.Right + AxisLabelGap / 2;
            dc.DrawText(text, new Point(x, lineY - text.Height / 2));
        }
    }

    private void DrawXLabels(DrawingContext dc, double dpi)
    {
        if (XLabels is not { } xLabels) return;

        for (var i = 0; i < xLabels.Count && i < MaxTickLabels; i++)
        {
            var text = Label(xLabels[i], false, dpi);
            var x = i switch
            {
                0 => _plot.Left,
                1 => _plot.Left + _plot.Width / 2 - text.Width / 2,
                _ => _plot.Right - text.Width
            };
            dc.DrawText(text, new Point(x, _plot.Bottom + XLabelGap));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (HitLegend(e.GetPosition(this)) is not { } name) return;

        if (!_dimmedSeries.Remove(name)) _dimmedSeries.Add(name);

        RebuildEmphasis();
        HideHover();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);

        var overLegend = HitLegend(position) is not null;
        Cursor = overLegend ? Cursors.Hand : null;

        if (overLegend || _pointCount < 2 || _plot.IsEmpty || position.X < _plot.Left || position.X > _plot.Right)
        {
            HideHover();
            return;
        }

        var nearest = (int)Math.Round((position.X - _plot.Left) / _plot.Width * (_pointCount - 1));
        var index = NearestIndexWithValue(Math.Clamp(nearest, 0, _pointCount - 1));
        if (index < 0)
        {
            HideHover();
            return;
        }

        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            UpdatePopup(index);
            RenderHover();
        }

        _popup.HorizontalOffset = position.X + PopupCursorOffset;
        _popup.VerticalOffset = position.Y + PopupCursorOffset;
        _popup.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Cursor = null;
        HideHover();
    }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 흐리게 처리한 상태(_dimmedSeries)는 이름 기준이라 새로고침으로 데이터가 바뀌어도 유지된다
        var chart = (GraphLine)d;
        chart.RebuildSeriesState();
        chart.HideHover(); // 이전 인덱스가 다른 시점을 가리키므로 닫는다
    }

    private string? HitLegend(Point position)
    {
        foreach (var (bounds, name) in _legendHits)
            if (bounds.Contains(position))
                return name;

        return null;
    }

    private void HideHover()
    {
        _popup.IsOpen = false;
        if (_hoverIndex >= 0)
        {
            _hoverIndex = -1;
            RenderHover();
        }
    }

    private void RebuildSeriesState()
    {
        _legendSeries.Clear();
        if (Series is { } all)
            foreach (var series in all)
                if (series.HasData)
                    _legendSeries.Add(series);

        RebuildEmphasis();
        EnsurePopupRows();
    }

    /// <summary>강조 시리즈 목록과 "시점별 강조 시리즈 값 존재" 표 — 마우스 이동 시 배열 조회만 하도록.</summary>
    private void RebuildEmphasis()
    {
        _emphasized.Clear();
        _pointCount = 0;
        foreach (var series in _legendSeries)
        {
            _pointCount = Math.Max(_pointCount, series.Values.Count);
            if (IsEmphasized(series)) _emphasized.Add(series);
        }

        if (_hasValueAt.Length != _pointCount)
            _hasValueAt = new bool[_pointCount]; // 시점 수가 바뀔 때만(기간 변경 등) 재할당
        else
            Array.Clear(_hasValueAt);

        foreach (var series in _emphasized)
        {
            var values = series.Values;
            for (var i = 0; i < values.Count; i++)
                if (values[i] is not null)
                    _hasValueAt[i] = true;
        }
    }

    /// <summary>시리즈 구성(이름·색)이 바뀐 경우에만 팝업 행을 다시 만든다 — 데이터 새로고침마다 요소를 새로 만들지 않는다.</summary>
    private void EnsurePopupRows()
    {
        if (_popupRows.Count == _legendSeries.Count)
        {
            var same = true;
            for (var i = 0; i < _popupRows.Count && same; i++)
                same = _popupRows[i].Name == _legendSeries[i].Name && _popupRows[i].Color == _legendSeries[i].Color;

            if (same) return;
        }

        foreach (var row in _popupRows) _popupContent.Children.Remove(row.Panel);

        _popupRows.Clear();
        foreach (var series in _legendSeries)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            panel.Children.Add(new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(2),
                Background = PaintFor(series.Color).Fill,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            panel.Children.Add(new TextBlock
                { Text = series.Name, Foreground = LabelBrush, FontSize = 12, MinWidth = 64 });
            var value = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold };
            panel.Children.Add(value);
            _popupContent.Children.Add(panel);
            _popupRows.Add(new PopupRow(series.Name, series.Color, panel, value));
        }
    }

    private void UpdatePopup(int index)
    {
        if (Timestamps is { } timestamps && index < timestamps.Count && timestamps[index] is { } time)
        {
            _popupTime.Text = time.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            _popupTime.Visibility = Visibility.Visible;
        }
        else
        {
            _popupTime.Visibility = Visibility.Collapsed;
        }

        for (var i = 0; i < _popupRows.Count && i < _legendSeries.Count; i++)
        {
            var series = _legendSeries[i];
            var row = _popupRows[i];
            if (IsEmphasized(series) && index < series.Values.Count && series.Values[index] is { } value)
            {
                row.Value.Text = FormatValue(series.Unit, value, false);
                row.Panel.Visibility = Visibility.Visible;
            }
            else
            {
                row.Panel.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>라벨·범례 실제 크기로 여백을 계산한 플롯 영역(좌 축 라벨, 우 축 라벨, 위 범례 줄).</summary>
    private Rect ComputePlotArea(
        double width, double height, double dpi, string[] leftLabels, string[]? rightLabels, bool hasLegend)
    {
        var labelHeight = Label("0", false, dpi).Height;
        var left = Math.Max(MinLeftPad, Math.Ceiling(MaxLabelWidth(leftLabels, dpi) + AxisLabelGap));
        var right = rightLabels is null ? RightPad : Math.Ceiling(MaxLabelWidth(rightLabels, dpi) + AxisLabelGap);

        // 맨 위 축 라벨은 격자선 중앙 정렬이라 절반이 위로 나간다 → 그만큼 + 범례 줄 확보
        var top = labelHeight / 2 + (hasLegend ? labelHeight + LegendGap : 0);
        var bottom = labelHeight + XLabelGap;

        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        return plotWidth > 0 && plotHeight > 0 ? new Rect(left, top, plotWidth, plotHeight) : Rect.Empty;
    }

    private double MaxLabelWidth(string[] labels, double dpi)
    {
        var max = 0.0;
        foreach (var label in labels) max = Math.Max(max, Label(label, false, dpi).WidthIncludingTrailingWhitespace);

        return max;
    }

    private bool HasAxis(GraphAxis axis)
    {
        foreach (var series in _legendSeries)
            if (series.Axis == axis)
                return true;

        return false;
    }

    /// <summary>축 최댓값: 해당 축이 모두 % 면 0–100 고정(매 틱 스케일 점프 방지), 아니면 해당 축 시리즈 최댓값을 딱 떨어지게 올림.</summary>
    private double AxisMax(GraphAxis axis)
    {
        var hasAxis = false;
        var allPercent = true;
        var unit = GraphValueUnit.Number;
        var max = 0.0;
        foreach (var series in _legendSeries)
        {
            if (series.Axis != axis) continue;

            if (!hasAxis)
            {
                unit = series.Unit;
                hasAxis = true;
            }

            allPercent &= series.Unit == GraphValueUnit.Percent;
            var values = series.Values;
            for (var i = 0; i < values.Count; i++)
                if (values[i] is { } v && v > max)
                    max = v;
        }

        if (hasAxis && allPercent) return PercentAxisMax;

        return unit == GraphValueUnit.BytesPerSecond ? NiceBinaryMax(max) : NiceDecimalMax(max);
    }

    private string[] AxisLabels(GraphAxis axis, double max)
    {
        var unit = GraphValueUnit.Percent;
        foreach (var series in _legendSeries)
            if (series.Axis == axis)
            {
                unit = series.Unit;
                break;
            }

        return
        [
            FormatValue(unit, 0, true),
            FormatValue(unit, max / 2, true),
            FormatValue(unit, max, true)
        ];
    }

    /// <summary>
    ///     바이트 축 최댓값을 1024 단위 기준 "딱 떨어지는" 짝수 값으로 올림 — 절반(중간 눈금)도 정수.
    ///     예: 1.98 MB → 2 MB(중간 1 MB), 700 KB → 800 KB(중간 400 KB).
    /// </summary>
    private static double NiceBinaryMax(double max)
    {
        if (max <= 0) return 2;

        var scale = 1.0;
        while (max / scale >= 1024) scale *= 1024;

        var mantissa = max / scale;
        foreach (var step in NiceBinarySteps)
            if (mantissa <= step)
                return step * scale;

        return 1024 * scale; // 도달하지 않음(마지막 단계가 1024)
    }

    /// <summary>일반 수치 축: 2·4·6·8·10 × 10^n 으로 올림(중간 눈금이 정수).</summary>
    private static double NiceDecimalMax(double max)
    {
        if (max <= 0) return 2;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var factor in NiceDecimalFactors)
            if (max <= factor * magnitude)
                return factor * magnitude;

        return 20 * magnitude;
    }

    private static string FormatValue(GraphValueUnit unit, double value, bool forAxis)
    {
        return unit switch
        {
            GraphValueUnit.Percent => forAxis ? $"{value:0.#}%" : $"{value:F1}%",
            GraphValueUnit.BytesPerSecond => $"{ByteFormatter.Format(value)}/s",
            _ => value.ToString("#,##0.##", CultureInfo.CurrentCulture)
        };
    }

    private double MaxFor(GraphSeries series)
    {
        return series.Axis == GraphAxis.Left ? _leftMax : _rightMax;
    }

    private Point ToPoint(int index, double value, int count, double max)
    {
        var x = _plot.Left + _plot.Width * index / (count - 1);
        var ratio = Math.Clamp(value / max, 0, 1);
        return new Point(x, _plot.Bottom - _plot.Height * ratio);
    }

    private bool IsEmphasized(GraphSeries series)
    {
        return !_dimmedSeries.Contains(series.Name);
    }

    /// <summary>강조 시리즈 중 하나라도 값이 있는 가장 가까운 시점(미리 계산한 표 조회). 없으면 -1.</summary>
    private int NearestIndexWithValue(int start)
    {
        for (var distance = 0; distance < _pointCount; distance++)
        {
            if (start - distance >= 0 && _hasValueAt[start - distance]) return start - distance;

            if (start + distance < _pointCount && _hasValueAt[start + distance]) return start + distance;
        }

        return -1;
    }

    private static SeriesPaint PaintFor(Color color)
    {
        if (!PaintCache.TryGetValue(color, out var paint))
        {
            var fill = Freeze(new SolidColorBrush(color));
            var line = Freeze(new Pen(fill, LineThickness) { LineJoin = PenLineJoin.Round });
            var dimBrush = Freeze(new SolidColorBrush(Color.FromArgb(DimmedAlpha, color.R, color.G, color.B)));
            var dimLine = Freeze(new Pen(dimBrush, LineThickness) { LineJoin = PenLineJoin.Round });
            paint = new SeriesPaint(fill, line, dimLine);
            PaintCache[color] = paint;
        }

        return paint;
    }

    /// <summary>라벨 FormattedText 캐시 — 같은 텍스트를 렌더마다(여백 계산·그리기 두 번씩) 새로 측정하지 않는다.</summary>
    private FormattedText Label(string text, bool dimmed, double dpi)
    {
        if (_labelCacheDpi != dpi || _labelCache.Count >= MaxCachedLabels)
        {
            _labelCache.Clear(); // DPI 변경 또는 축 라벨 교체가 누적되면 비움
            _labelCacheDpi = dpi;
        }

        if (!_labelCache.TryGetValue((text, dimmed), out var formatted))
        {
            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelTypeface,
                LabelFontSize, dimmed ? DimmedLabelBrush : LabelBrush, dpi);
            _labelCache[(text, dimmed)] = formatted;
        }

        return formatted;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private sealed record SeriesPaint(Brush Fill, Pen Line, Pen DimLine);

    private sealed record PopupRow(string Name, Color Color, StackPanel Panel, TextBlock Value);
}
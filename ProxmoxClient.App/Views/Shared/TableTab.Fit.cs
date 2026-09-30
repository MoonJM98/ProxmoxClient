using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     열 너비 맞추기 — 너비를 정한 열도 머리글·값이 잘리지 않게 넓힌다(MAC·주소 등). 아주 긴 값은
///     <c>MaxAutoColumnWidth</c> 에서 멈추고 말줄임(…)·툴팁으로 본다. 합이 창보다 넓으면 가로 스크롤이 생긴다.
/// </summary>
public partial class TableTab
{
    /// <summary>칸 안쪽 여백(좌우) + 머리글 정렬 표시 자리.</summary>
    private const double CellPadding = 26;

    private void FitColumns(IReadOnlyList<TableRow> rows)
    {
        var typeface = new Typeface(TableGrid.FontFamily, TableGrid.FontStyle, FontWeights.Normal,
            TableGrid.FontStretch);
        var headerFace = new Typeface(TableGrid.FontFamily, TableGrid.FontStyle, FontWeights.SemiBold,
            TableGrid.FontStretch);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var i = 0; i < _columns.Count && i < TableGrid.Columns.Count; i++)
        {
            var column = _columns[i];
            if (column.Width <= 0) continue; // 채움 열은 남은 자리를 쓴다

            var widest = TextWidth(Localization.Loc.T(column.HeaderKey), headerFace, dpi);
            foreach (var row in rows) widest = Math.Max(widest, TextWidth(row[column.Key], typeface, dpi));
            TableGrid.Columns[i].Width = new DataGridLength(
                Math.Clamp(widest + CellPadding, column.Width, Math.Max(column.Width, MaxAutoColumnWidth)));
        }
    }

    private double TextWidth(string text, Typeface typeface, double dpi)
    {
        if (text.Length == 0) return 0;

        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
            TableGrid.FontSize, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;
    }
}

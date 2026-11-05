using Avalonia;
using Avalonia.Media;

namespace ProxmoxClient.UI.Views.Terminal;

/// <summary>
///     선 그리기(U+2500~257F)·블록(U+2580~259F) 문자를 칸에 꼭 맞는 사각형으로 그린다 — 글꼴 글리프는 줄 높이보다
///     짧아 mc·htop 틀이 점선처럼 끊기기 때문이다(xterm.js·Windows Terminal 도 같은 방식).
/// </summary>
internal static class BoxDrawing
{
    // 위·오른쪽·아래·왼쪽 팔: 0 없음, 1 가는 선, 2 굵은 선, 3 이중 선. U+2500 부터 차례로.
    private const string Arms =
        "0101 0202 1010 2020 0101 0202 1010 2020 0101 0202 1010 2020 " + // ─━│┃ 점선들
        "0110 0210 0120 0220 0011 0012 0021 0022 1100 1200 2100 2200 1001 1002 2001 2002 " + // ┌…┛
        "1110 1210 2110 1120 2120 2210 1220 2220 1011 1012 2011 1021 2021 2012 1022 2022 " + // ├…┫
        "0111 0112 0211 0212 0121 0122 0221 0222 1101 1102 1201 1202 2101 2102 2201 2202 " + // ┬…┻
        "1111 1112 1211 1212 2111 1121 2121 2112 2211 1122 1221 2212 1222 2122 2221 2222 " + // ┼…╋
        "0101 0202 1010 2020 " + // ╌╍╎╏
        "0303 3030 0310 0130 0330 0013 0031 0033 1300 3100 3300 1003 3001 3003 " + // ═…╝
        "1310 3130 3330 1013 3031 3033 0313 0131 0333 1303 3101 3303 1313 3131 3333 " + // ╞…╬
        "0110 0011 1001 1100 0000 0000 0000 " + // ╭╮╯╰ (둥근 모서리는 꺾인 선으로), 대각선은 글꼴로
        "0001 1000 0100 0010 0002 2000 0200 0020 0201 1020 0102 2010"; // ╴…╿

    private static readonly byte[] Table = Build();

    public static bool Handles(int rune)
    {
        return rune is >= 0x2500 and <= 0x259F && (rune >= 0x2580 || Table[rune - 0x2500] != 0);
    }

    public static bool TryDraw(DrawingContext context, int rune, IBrush brush, Rect cell, double fontSize)
    {
        if (rune is >= 0x2580 and <= 0x259F) return Block(context, rune, brush, cell);
        if (rune is < 0x2500 or > 0x257F) return false;

        var arms = Table[rune - 0x2500];
        if (arms == 0) return false;

        var light = Math.Max(1, Math.Round(fontSize / 14));
        var cx = Math.Round(cell.X + cell.Width / 2 - light / 2);
        var cy = Math.Round(cell.Y + cell.Height / 2 - light / 2);
        var left = Math.Round(cell.X);
        var right = Math.Round(cell.Right);
        var top = cell.Y;
        var bottom = cell.Bottom;

        var up = arms >> 6 & 3;
        var rightArm = arms >> 4 & 3;
        var down = arms >> 2 & 3;
        var leftArm = arms & 3;

        Vertical(up, top, cy + Thick(up, light));
        Vertical(down, cy, bottom);
        Horizontal(leftArm, left, cx + Thick(leftArm, light));
        Horizontal(rightArm, cx, right);
        return true;

        void Vertical(int kind, double y0, double y1)
        {
            if (kind == 0) return;

            var t = Thick(kind, light);
            if (kind == 3)
            {
                context.FillRectangle(brush, new Rect(cx - light, y0, light, y1 - y0));
                context.FillRectangle(brush, new Rect(cx + light, y0, light, y1 - y0));
                return;
            }

            context.FillRectangle(brush, new Rect(cx - (t - light) / 2, y0, t, y1 - y0));
        }

        void Horizontal(int kind, double x0, double x1)
        {
            if (kind == 0) return;

            var t = Thick(kind, light);
            if (kind == 3)
            {
                context.FillRectangle(brush, new Rect(x0, cy - light, x1 - x0, light));
                context.FillRectangle(brush, new Rect(x0, cy + light, x1 - x0, light));
                return;
            }

            context.FillRectangle(brush, new Rect(x0, cy - (t - light) / 2, x1 - x0, t));
        }
    }

    private static double Thick(int kind, double light)
    {
        return kind switch { 2 => light * 2, 3 => light * 2, _ => light };
    }

    /// <summary>블록 요소: ▀▁…█ ▉…▏ ▐ ░▒▓ ▔▕ 사분면.</summary>
    private static bool Block(DrawingContext context, int rune, IBrush brush, Rect cell)
    {
        var x = Math.Round(cell.X);
        var r = Math.Round(cell.Right);
        var w = r - x;
        var y = cell.Y;
        var h = cell.Height;

        void Fill(double fx, double fy, double fw, double fh)
        {
            context.FillRectangle(brush, new Rect(x + Math.Round(fx * w), y + Math.Round(fy * h),
                Math.Round((fx + fw) * w) - Math.Round(fx * w), Math.Round((fy + fh) * h) - Math.Round(fy * h)));
        }

        switch (rune)
        {
            case 0x2580:
                Fill(0, 0, 1, 0.5);
                return true;
            case >= 0x2581 and <= 0x2588: // 아래에서 n/8
                var n = (rune - 0x2580) / 8.0;
                Fill(0, 1 - n, 1, n);
                return true;
            case >= 0x2589 and <= 0x258F: // 왼쪽에서 (8-k)/8
                var k = (0x2590 - rune) / 8.0;
                Fill(0, 0, k, 1);
                return true;
            case 0x2590:
                Fill(0.5, 0, 0.5, 1);
                return true;
            case 0x2591 or 0x2592 or 0x2593:
                var alpha = rune switch { 0x2591 => 0.25, 0x2592 => 0.5, _ => 0.75 };
                using (context.PushOpacity(alpha))
                {
                    Fill(0, 0, 1, 1);
                }

                return true;
            case 0x2594:
                Fill(0, 0, 1, 1 / 8.0);
                return true;
            case 0x2595:
                Fill(7 / 8.0, 0, 1 / 8.0, 1);
                return true;
            case >= 0x2596 and <= 0x259F:
                // 사분면: 비트 = 왼위 1, 오위 2, 왼아래 4, 오아래 8
                int[] quads = [4, 8, 1, 1 | 4 | 8, 1 | 8, 1 | 2 | 4, 1 | 2 | 8, 2, 2 | 4, 2 | 4 | 8];
                var q = quads[rune - 0x2596];
                if ((q & 1) != 0) Fill(0, 0, 0.5, 0.5);
                if ((q & 2) != 0) Fill(0.5, 0, 0.5, 0.5);
                if ((q & 4) != 0) Fill(0, 0.5, 0.5, 0.5);
                if ((q & 8) != 0) Fill(0.5, 0.5, 0.5, 0.5);
                return true;
        }

        return false;
    }

    private static byte[] Build()
    {
        var table = new byte[0x80];
        var entries = Arms.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < entries.Length && i < table.Length; i++)
        {
            var e = entries[i];
            table[i] = (byte)((e[0] - '0') << 6 | (e[1] - '0') << 4 | (e[2] - '0') << 2 | (e[3] - '0'));
        }

        return table;
    }
}

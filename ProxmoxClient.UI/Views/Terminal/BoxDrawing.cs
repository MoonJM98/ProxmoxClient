using Avalonia;
using Avalonia.Media;

namespace ProxmoxClient.UI.Views.Terminal;

/// <summary>
///     선 그리기(U+2500~257F)·블록(U+2580~259F)·점자(U+2800~28FF)·6분할 블록(U+1FB00~1FB3B)·Powerline(U+E0B0~E0B7)
///     문자를 칸에 꼭 맞는 도형으로 그린다 — 글꼴 글리프는 줄 높이보다 짧아 mc·htop 틀이 점선처럼 끊기기 때문이다
///     (xterm.js·Windows Terminal 도 같은 방식).
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
        "0110 0011 1001 1100 0000 0000 0000 " + // ╭╮╯╰(둥근 모서리는 따로 호로 그린다), 대각선 ╱╲╳ 도 따로
        "0001 1000 0100 0010 0002 2000 0200 0020 0201 1020 0102 2010"; // ╴…╿

    private static readonly byte[] Table = Build();

    public static bool Handles(int rune)
    {
        return rune is >= 0x2500 and <= 0x259F or >= 0x2800 and <= 0x28FF or >= 0xE0B0 and <= 0xE0B7
            or >= 0x1FB00 and <= 0x1FB3B;
    }

    public static bool TryDraw(DrawingContext context, int rune, IBrush brush, Rect cell, double fontSize)
    {
        switch (rune)
        {
            case >= 0x2580 and <= 0x259F:
                return Block(context, rune, brush, cell);
            case >= 0x2800 and <= 0x28FF:
                Braille(context, rune - 0x2800, brush, cell);
                return true;
            case >= 0x1FB00 and <= 0x1FB3B:
                Sextant(context, rune - 0x1FB00, brush, cell);
                return true;
            case >= 0xE0B0 and <= 0xE0B7:
                Powerline(context, rune, brush, cell, Light(fontSize));
                return true;
            case < 0x2500 or > 0x257F:
                return false;
        }

        var light = Light(fontSize);
        if (rune is >= 0x256D and <= 0x2570)
        {
            Rounded(context, rune, brush, cell, light);
            return true;
        }

        if (rune is >= 0x2571 and <= 0x2573)
        {
            Diagonal(context, rune, brush, cell, light);
            return true;
        }

        var arms = Table[rune - 0x2500];
        if (arms == 0) return false;

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

        // 점선(┄┅┆┇┈┉┊┋╌╍╎╏) — 칸을 n 토막으로 나눠 토막마다 가운데에 선을, 양 끝에 틈 반씩(이웃 칸과 같은 간격)
        var dashes = rune switch
        {
            >= 0x2504 and <= 0x2507 => 3, >= 0x2508 and <= 0x250B => 4, >= 0x254C and <= 0x254F => 2, _ => 0
        };
        if (dashes > 0)
        {
            var t = Thick(up | leftArm, light);
            if (leftArm != 0)
                for (var i = 0; i < dashes; i++)
                {
                    var x0 = Math.Round(left + (right - left) * (i + 0.25) / dashes);
                    var x1 = Math.Round(left + (right - left) * (i + 0.75) / dashes);
                    context.FillRectangle(brush, new Rect(x0, cy - (t - light) / 2, x1 - x0, t));
                }
            else
                for (var i = 0; i < dashes; i++)
                {
                    var y0 = Math.Round(top + (bottom - top) * (i + 0.25) / dashes);
                    var y1 = Math.Round(top + (bottom - top) * (i + 0.75) / dashes);
                    context.FillRectangle(brush, new Rect(cx - (t - light) / 2, y0, t, y1 - y0));
                }

            return true;
        }

        // 이중 선의 두 줄: 세로는 a·b(왼쪽 끝 x), 가로는 c·d(위 끝 y) — 두께는 light
        var a = cx - light;
        var b = cx + light;
        var c = cy - light;
        var d = cy + light;
        var vDouble = up == 3 || down == 3;
        var hDouble = leftArm == 3 || rightArm == 3;

        // 위 팔
        if (up == 3)
        {
            // 왼쪽 줄: 왼쪽에 이중 팔이 있으면 안쪽 모서리, 아래로 이어지면 끝까지, 오른쪽 이중 팔만 있으면 바깥 모서리
            VLine(a, top, leftArm == 3 ? c + light : down != 0 ? cy + light : rightArm == 3 ? d + light : cy + light);
            VLine(b, top, rightArm == 3 ? c + light : down != 0 ? cy + light : leftArm == 3 ? d + light : cy + light);
        }
        else if (up != 0)
        {
            var end = cy + Thick(up, light);
            if (hDouble) end = down != 0 ? d + light : leftArm == 3 && rightArm == 3 ? c + light : d + light;
            Vertical(up, top, end);
        }

        // 아래 팔
        if (down == 3)
        {
            VLine(a, leftArm == 3 ? d : up != 0 ? cy : rightArm == 3 ? c : cy, bottom);
            VLine(b, rightArm == 3 ? d : up != 0 ? cy : leftArm == 3 ? c : cy, bottom);
        }
        else if (down != 0)
        {
            var start = cy;
            if (hDouble) start = up != 0 ? c : leftArm == 3 && rightArm == 3 ? d : c;
            Vertical(down, start, bottom);
        }

        // 왼쪽 팔
        if (leftArm == 3)
        {
            HLine(c, left, up == 3 ? a + light : rightArm != 0 ? cx + light : down == 3 ? b + light : cx + light);
            HLine(d, left, down == 3 ? a + light : rightArm != 0 ? cx + light : up == 3 ? b + light : cx + light);
        }
        else if (leftArm != 0)
        {
            var end = cx + Thick(leftArm, light);
            if (vDouble) end = rightArm != 0 ? b + light : up == 3 && down == 3 ? a + light : b + light;
            Horizontal(leftArm, left, end);
        }

        // 오른쪽 팔
        if (rightArm == 3)
        {
            HLine(c, up == 3 ? b : leftArm != 0 ? cx : down == 3 ? a : cx, right);
            HLine(d, down == 3 ? b : leftArm != 0 ? cx : up == 3 ? a : cx, right);
        }
        else if (rightArm != 0)
        {
            var start = cx;
            if (vDouble) start = leftArm != 0 ? a : up == 3 && down == 3 ? b : a;
            Horizontal(rightArm, start, right);
        }

        return true;

        void VLine(double x, double y0, double y1)
        {
            context.FillRectangle(brush, new Rect(x, y0, light, y1 - y0));
        }

        void HLine(double y, double x0, double x1)
        {
            context.FillRectangle(brush, new Rect(x0, y, x1 - x0, light));
        }

        void Vertical(int kind, double y0, double y1)
        {
            var t = Thick(kind, light);
            context.FillRectangle(brush, new Rect(cx - (t - light) / 2, y0, t, y1 - y0));
        }

        void Horizontal(int kind, double x0, double x1)
        {
            var t = Thick(kind, light);
            context.FillRectangle(brush, new Rect(x0, cy - (t - light) / 2, x1 - x0, t));
        }
    }

    private static double Light(double fontSize) => Math.Max(1, Math.Round(fontSize / 14));

    private static double Thick(int kind, double light)
    {
        return kind switch { 2 => light * 2, 3 => light * 2, _ => light };
    }

    /// <summary>둥근 모서리 ╭╮╯╰ — 곧은 선과 같은 자리·두께로 이어지게, 칸 가운데에서 1/4 원.</summary>
    private static void Rounded(DrawingContext context, int rune, IBrush brush, Rect cell, double light)
    {
        var cx = Math.Round(cell.X + cell.Width / 2 - light / 2) + light / 2; // 선 한가운데
        var cy = Math.Round(cell.Y + cell.Height / 2 - light / 2) + light / 2;
        var left = Math.Round(cell.X);
        var right = Math.Round(cell.Right);
        var r = Math.Min(right - cx, cx - left);
        var toRight = rune is 0x256D or 0x2570; // ╭╰ 은 오른쪽으로
        var toDown = rune is 0x256D or 0x256E; // ╭╮ 은 아래로
        var hx = toRight ? right : left;
        var vy = toDown ? cell.Bottom : cell.Y;
        var arcStart = new Point(cx + (toRight ? r : -r), cy);
        var arcEnd = new Point(cx, cy + (toDown ? r : -r));
        // 가로 끝 → 호 → 세로 끝. 돌아가는 방향은 모서리마다 다르다
        var sweep = rune is 0x256D or 0x256F ? SweepDirection.CounterClockwise : SweepDirection.Clockwise;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(hx, cy), false);
            g.LineTo(arcStart);
            g.ArcTo(arcEnd, new Size(r, r), 0, false, sweep);
            g.LineTo(new Point(cx, vy));
            g.EndFigure(false);
        }

        using (context.PushClip(new Rect(left, cell.Y, right - left, cell.Height)))
        {
            context.DrawGeometry(null, new Pen(brush, light), geometry);
        }
    }

    /// <summary>대각선 ╱╲╳ — 칸 모서리에서 모서리로(이웃 칸과 이어진다).</summary>
    private static void Diagonal(DrawingContext context, int rune, IBrush brush, Rect cell, double light)
    {
        var pen = new Pen(brush, light * 1.2);
        using (context.PushClip(cell))
        {
            if (rune is 0x2571 or 0x2573) context.DrawLine(pen, cell.BottomLeft, cell.TopRight);
            if (rune is 0x2572 or 0x2573) context.DrawLine(pen, cell.TopLeft, cell.BottomRight);
        }
    }

    /// <summary>점자 — 2×4 점. 비트: 1 2 3 7 은 왼쪽 위에서 아래로, 4 5 6 8 은 오른쪽.</summary>
    private static void Braille(DrawingContext context, int bits, IBrush brush, Rect cell)
    {
        if (bits == 0) return;

        var w = cell.Width / 2;
        var h = cell.Height / 4;
        var size = Math.Max(1, Math.Round(Math.Min(w, h) * 0.55));
        ReadOnlySpan<int> col = [0, 0, 0, 1, 1, 1, 0, 1];
        ReadOnlySpan<int> row = [0, 1, 2, 0, 1, 2, 3, 3];
        for (var i = 0; i < 8; i++)
        {
            if ((bits & 1 << i) == 0) continue;

            var x = Math.Round(cell.X + w * (col[i] + 0.5) - size / 2);
            var y = Math.Round(cell.Y + h * (row[i] + 0.5) - size / 2);
            context.FillRectangle(brush, new Rect(x, y, size, size));
        }
    }

    /// <summary>6분할 블록(🬀…🬻) — 2×3 칸. 값 1~62 에서 ▌(21)·▐(42) 를 뺀 차례.</summary>
    private static void Sextant(DrawingContext context, int index, IBrush brush, Rect cell)
    {
        var bits = index + 1;
        if (bits >= 21) bits++;
        if (bits >= 42) bits++;

        var x = Math.Round(cell.X);
        var mid = Math.Round(cell.X + cell.Width / 2);
        var r = Math.Round(cell.Right);
        double[] ys = [cell.Y, Math.Round(cell.Y + cell.Height / 3), Math.Round(cell.Y + cell.Height * 2 / 3), cell.Bottom];
        for (var i = 0; i < 6; i++)
        {
            if ((bits & 1 << i) == 0) continue;

            var (x0, x1) = i % 2 == 0 ? (x, mid) : (mid, r);
            context.FillRectangle(brush, new Rect(x0, ys[i / 2], x1 - x0, ys[i / 2 + 1] - ys[i / 2]));
        }
    }

    /// <summary>Powerline 화살표·반원(E0B0~E0B7) — 칸 높이를 꽉 채워 이웃 바탕과 이어진다.</summary>
    private static void Powerline(DrawingContext context, int rune, IBrush brush, Rect cell, double light)
    {
        var x = Math.Round(cell.X);
        var r = Math.Round(cell.Right);
        var top = cell.Y;
        var bottom = cell.Bottom;
        var midY = (top + bottom) / 2;
        var pointRight = rune is 0xE0B0 or 0xE0B1 or 0xE0B4 or 0xE0B5;
        var solid = rune is 0xE0B0 or 0xE0B2 or 0xE0B4 or 0xE0B6;
        var round = rune >= 0xE0B4;
        var (flat, tip) = pointRight ? (x, r) : (r, x);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(flat, top), solid);
            if (round)
                g.ArcTo(new Point(flat, bottom), new Size(Math.Abs(tip - flat), (bottom - top) / 2), 0, false,
                    pointRight ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
            else
            {
                g.LineTo(new Point(tip, midY));
                g.LineTo(new Point(flat, bottom));
            }

            g.EndFigure(solid);
        }

        using (context.PushClip(new Rect(x, top, r - x, bottom - top)))
        {
            context.DrawGeometry(solid ? brush : null, solid ? null : new Pen(brush, light), geometry);
        }
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

namespace ProxmoxClient.UI.Views.Terminal;

/// <summary>
///     창 크기가 바뀔 때 주 화면·스크롤백 다시 접기(Windows Terminal 과 비슷하다).
///     <para>
///         자동 줄바꿈(<see cref="Line.Wrapped" />)으로 이어진 줄을 한 줄(논리 줄)로 보고 새 열 수로 다시 자른다. 늘 논리 줄에서
///         다시 접으므로 좁혔다 넓혀도(전화기 회전·창 끌기·키보드 애니메이션) 줄 끝이 사라지지 않는다.
///     </para>
/// </summary>
public sealed partial class TerminalBuffer
{
    // 새 자리를 찾을 위치 — 커서, 저장한 커서, 화면 맨 위 줄, 위로 올려 보던 맨 위 줄
    private const int MarkCursor = 0, MarkSaved = 1, MarkScreenTop = 2, MarkView = 3, MarkCount = 4;

    private struct Mark
    {
        public bool Used;
        public int Line; // 옛 줄 번호(스크롤백 + 화면)
        public int Col;
        public bool Pending; // 줄 끝에서 다음 글자를 기다리는 중(다음 글자는 다음 칸)
        public int Offset; // 논리 줄 안 칸 위치
        public int NewLine;
        public int NewCol;
    }

    /// <summary>
    ///     주 화면 다시 접기 + 화면 높이 맞추기. 커서 줄은 화면에 남기고, 넘치는 위 줄은 스크롤백으로 보내고, 줄이 늘면 스크롤백에서
    ///     가져온다. 돌려주는 값은 새 <paramref name="scrollOffset" />.
    /// </summary>
    private int Reflow(int columns, int rows, int scrollOffset)
    {
        var sb = _scrollback.Count;
        var total = sb + Rows;
        Line Old(int i) => i < sb ? _scrollback[i] : _main[i - sb];

        var marks = new Mark[MarkCount];
        if (!IsAlternateScreen)
            marks[MarkCursor] = new Mark { Used = true, Line = sb + _y, Col = Math.Min(_x, Columns - 1), Pending = _pendingWrap };
        if (_savedMain != default)
            marks[MarkSaved] = new Mark
            {
                Used = true, Line = sb + Math.Clamp(_savedMain.Y, 0, Rows - 1), Col = Math.Min(_savedMain.X, Columns - 1),
                Pending = _savedMain.PendingWrap
            };
        marks[MarkScreenTop] = new Mark { Used = true, Line = sb };
        if (scrollOffset > 0) marks[MarkView] = new Mark { Used = true, Line = Math.Max(0, sb - scrollOffset) };

        List<Line> lines;
        if (columns == Columns)
        {
            // 열 수가 같으면 줄은 그대로 — 높이만 맞춘다
            lines = new List<Line>(total + rows);
            for (var i = 0; i < total; i++) lines.Add(Old(i));
            for (var m = 0; m < MarkCount; m++)
                if (marks[m].Used)
                {
                    marks[m].NewLine = marks[m].Line;
                    marks[m].NewCol = marks[m].Col;
                }
        }
        else
        {
            lines = new List<Line>(total + total / 2 + rows);
            var lenBuffer = new int[16];
            for (var a = 0; a < total;)
            {
                var b = a;
                while (b < total - 1 && Old(b).Wrapped) b++;
                if (b - a + 1 > lenBuffer.Length) lenBuffer = new int[Math.Max(b - a + 1, lenBuffer.Length * 2)];
                ReflowRun(a, b, Old, lenBuffer, marks, columns, lines);
                a = b + 1;
            }
        }

        FinishMarks(marks, columns);

        // 높이 맞추기 — 옛 화면 맨 위 줄이 새 화면 맨 위(top)
        var top = marks[MarkScreenTop].NewLine;
        var cursor = marks[IsAlternateScreen ? MarkSaved : MarkCursor] is { Used: true } c ? c.NewLine : -1;
        var height = lines.Count - top;
        if (height > rows)
        {
            // 커서 아래 빈 줄부터 버리고, 그래도 많으면 위 줄을 스크롤백으로
            for (var i = lines.Count - 1; i > cursor && i > top && height > rows; i--)
            {
                if (!lines[i].IsBlank || lines[i - 1].Wrapped) break;

                lines.RemoveAt(i);
                height--;
            }

            if (height > rows) top += height - rows;
        }
        else if (height < rows)
        {
            // 스크롤백에서 되가져오고(커서도 그만큼 내려간다), 모자라면 아래에 빈 줄
            var pull = Math.Min(rows - height, top);
            top -= pull;
            for (var i = height + pull; i < rows; i++) lines.Add(new Line(columns, TerminalColor.Default));
        }

        // 스크롤백 한도
        var drop = Math.Max(0, top - _scrollbackLimit);
        ScrolledLines += top - sb;
        DroppedLines += drop;
        _scrollback.Clear();
        if (_scrollback.Capacity < top - drop) _scrollback.Capacity = top - drop;
        for (var i = drop; i < top; i++) _scrollback.Add(lines[i]);
        var screen = new Line[rows];
        for (var i = 0; i < rows; i++) screen[i] = lines[top + i];
        _main = screen;

        if (marks[MarkCursor] is { Used: true } cur)
        {
            _y = cur.NewLine - top;
            _x = Math.Min(cur.NewCol, columns - 1);
            _pendingWrap = cur.NewCol >= columns;
        }

        if (marks[MarkSaved] is { Used: true } saved)
        {
            var y = Math.Clamp(saved.NewLine - top, 0, rows - 1);
            _savedMain = _savedMain with
            {
                X = Math.Min(saved.NewCol, columns - 1), Y = y, PendingWrap = saved.NewCol >= columns
            };
        }

        if (scrollOffset <= 0) return 0;

        var newSb = _scrollback.Count;
        return Math.Clamp(newSb - (marks[MarkView].NewLine - drop), 0, newSb);
    }

    /// <summary>
    ///     자동 줄바꿈으로 이어진 옛 줄 a~b(논리 줄 하나)를 새 열 수로 잘라 <paramref name="output" /> 에 더한다.
    ///     <para>
    ///         마지막 조각 끝의 기본 빈칸은 버린다(배경색이 있는 빈칸은 글자로 본다). 넓은 글자가 줄 끝에 걸리면 다음 줄로 넘기고 그 자리는
    ///         빈칸(Print 와 같다) — 이어 붙일 때 그 빈칸은 건너뛴다.
    ///     </para>
    /// </summary>
    private static void ReflowRun(int a, int b, Func<int, Line> old, int[] lens, Mark[] marks, int columns, List<Line> output)
    {
        // 조각마다 실제 글자 칸 수
        var content = 0;
        for (var i = a; i <= b; i++)
        {
            var line = old(i);
            int len;
            if (i < b)
            {
                len = line.Cells.Length;
                if (len > 0 && IsPad(line.Cells[len - 1]) && old(i + 1).Cells is [{ Width: 2 }, ..]) len--;
            }
            else
            {
                len = TrimmedLength(line.Cells);
            }

            lens[i - a] = len;
            content += len;
        }

        // 이 논리 줄에 있는 위치 → 논리 칸 위치. 커서가 글자 뒤 빈칸에 있으면 그만큼도 줄에 넣는다(되돌려도 같은 자리)
        var length = content;
        for (var m = 0; m < marks.Length; m++)
        {
            ref var mark = ref marks[m];
            if (!mark.Used || mark.Line < a || mark.Line > b) continue;

            var offset = 0;
            for (var i = a; i < mark.Line; i++) offset += lens[i - a];
            var col = mark.Line < b ? Math.Min(mark.Col, lens[mark.Line - a]) : mark.Col;
            mark.Offset = offset + col;
            if (mark.Line == b || col < lens[mark.Line - a]) length = Math.Max(length, mark.Offset + 1);
        }

        // 한 줄에 다 들어가면 줄을 그대로 쓴다
        if (a == b && length <= columns)
        {
            var line = old(a);
            line.Resize(columns);
            line.Wrapped = false;
            for (var m = 0; m < marks.Length; m++)
                if (marks[m].Used && marks[m].Line == a)
                {
                    marks[m].NewLine = output.Count;
                    marks[m].NewCol = marks[m].Offset;
                }

            output.Add(line);
            return;
        }

        var src = a;
        var srcCol = 0;
        var left = content; // 아직 옮기지 않은 글자 칸
        var pos = 0; // 논리 칸 위치
        do
        {
            var piece = new Line(columns, TerminalColor.Default);
            var take = Math.Min(columns, length - pos);
            var filled = 0;
            while (filled < take && left > 0)
            {
                if (srcCol >= lens[src - a])
                {
                    src++;
                    srcCol = 0;
                    continue;
                }

                var n = Math.Min(Math.Min(take - filled, lens[src - a] - srcCol), left);
                CopyCells(old(src), srcCol, piece, filled, n);
                filled += n;
                srcCol += n;
                left -= n;
            }

            // 넓은 글자의 앞 칸이 줄 끝에 걸렸다 — 다음 줄로(그 칸은 빈칸)
            if (filled == columns && columns > 1 && piece.Cells[columns - 1].Width == 2)
            {
                piece.Cells[columns - 1] = TerminalCell.Blank(TerminalColor.Default);
                srcCol--;
                left++;
                take--;
            }

            for (var m = 0; m < marks.Length; m++)
            {
                ref var mark = ref marks[m];
                if (!mark.Used || mark.Line < a || mark.Line > b) continue;
                if (mark.Offset < pos || mark.Offset >= pos + take && pos + take < length) continue;

                mark.NewLine = output.Count;
                mark.NewCol = mark.Offset - pos;
                mark.Line = -1; // 찾았다
            }

            pos += take;
            piece.Wrapped = pos < length;
            output.Add(piece);
        } while (pos < length);
    }

    /// <summary>줄 끝에서 다음 글자를 기다리던 위치는 한 칸 뒤 — 줄 끝이면 그대로 기다린다(NewCol == columns).</summary>
    private static void FinishMarks(Mark[] marks, int columns)
    {
        for (var m = 0; m < marks.Length; m++)
        {
            ref var mark = ref marks[m];
            if (!mark.Used) continue;

            mark.NewCol = Math.Min(mark.NewCol, columns - 1);
            if (mark.Pending) mark.NewCol++;
        }
    }

    /// <summary>
    ///     칸 옮기기 — 다시 접을 때 칸은 모두 여기로 옮긴다(칸 말고 줄에 딸린 정보가 생기면 함께 옮길 곳).
    /// </summary>
    private static void CopyCells(Line src, int srcCol, Line dst, int dstCol, int count)
    {
        Array.Copy(src.Cells, srcCol, dst.Cells, dstCol, count);
        // 결합 문자(자소·악센트·이모지 묶음)도 같이 옮긴다
        if (src.Extra is null) return;

        for (var i = 0; i < count; i++)
            if (src.GetExtra(srcCol + i) is { } extra)
                dst.SetExtra(dstCol + i, extra);
    }

    /// <summary>넓은 글자를 다음 줄로 넘기며 비운 칸(지운 칸과 같다 — 직접 친 공백은 ' ').</summary>
    private static bool IsPad(in TerminalCell cell) => cell is { Rune: 0, Width: 1 };

    /// <summary>줄 끝 기본 빈칸을 뺀 길이 — 배경색·밑줄·반전이 있는 빈칸, 넓은 글자의 뒤 칸은 남긴다.</summary>
    private static int TrimmedLength(TerminalCell[] cells)
    {
        var n = cells.Length;
        while (n > 0)
        {
            var c = cells[n - 1];
            if (c.Width != 1 || c.Rune is not (0 or ' ') || c.Background != TerminalColor.Default ||
                (c.Flags & (CellFlags.Inverse | CellFlags.Underline | CellFlags.Strike)) != 0)
                break;
            n--;
        }

        return n;
    }
}

namespace ProxmoxClient.Core.Vnc;

/// <summary>
///     화면 변경 영역 모음 — 최대 <see cref="Capacity" /> 개 사각형을 유지하고, 넘치면 합쳤을 때 늘어나는 면적이
///     가장 작은 두 사각형을 병합한다. 모든 변경을 하나로 감싸던 방식은 떨어진 작은 영역 둘(좌상단 커서·우하단 시계)만
///     바뀌어도 화면 전체 크기를 복사했다. 겹치거나 합쳐도 면적이 늘지 않는 사각형은 추가 시점에 합친다.
///     스레드 안전하지 않음 — 호출자가 동기화한다. 할당 없음(고정 배열).
/// </summary>
public sealed class DirtyRegion
{
    public const int Capacity = 8;

    // 병합 전 잠시 Capacity+1 개가 된다
    private readonly Rect[] _rects = new Rect[Capacity + 1];

    public int Count { get; private set; }

    public Rect this[int index] => _rects[index];

    public void Clear()
    {
        Count = 0;
    }

    public void Add(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        var added = new Rect(x, y, x + width, y + height);
        for (var i = 0; i < Count;)
        {
            var existing = _rects[i];
            if (existing.Contains(added)) return;

            // 합쳐도 두 영역 면적 합보다 커지지 않으면(겹침·맞닿음) 하나로 — 겹친 부분을 두 번 복사하지 않는다
            var union = existing.Union(added);
            if (union.Area <= existing.Area + added.Area)
            {
                added = union;
                RemoveAt(i);
                i = 0; // 커진 영역이 앞의 사각형과 새로 겹칠 수 있으므로 처음부터 다시 확인
                continue;
            }

            i++;
        }

        _rects[Count++] = added;
        if (Count > Capacity) MergeCheapestPair();
    }

    /// <summary>현재 사각형들을 복사하고 개수를 돌려준다.</summary>
    public int CopyTo(Span<Rect> destination)
    {
        var count = Math.Min(Count, destination.Length);
        _rects.AsSpan(0, count).CopyTo(destination);
        return count;
    }

    private void MergeCheapestPair()
    {
        var bestI = 0;
        var bestJ = 1;
        var bestGrowth = long.MaxValue;
        for (var i = 0; i < Count; i++)
        for (var j = i + 1; j < Count; j++)
        {
            var growth = _rects[i].Union(_rects[j]).Area - _rects[i].Area - _rects[j].Area;
            if (growth < bestGrowth)
            {
                bestGrowth = growth;
                bestI = i;
                bestJ = j;
            }
        }

        _rects[bestI] = _rects[bestI].Union(_rects[bestJ]);
        RemoveAt(bestJ);
    }

    private void RemoveAt(int index)
    {
        Count--;
        _rects[index] = _rects[Count]; // 순서는 의미 없음 — 마지막 항목으로 채움
    }

    /// <summary>반열린 사각형 [X1, X2) × [Y1, Y2).</summary>
    public readonly record struct Rect(int X1, int Y1, int X2, int Y2)
    {
        public int Width => X2 - X1;
        public int Height => Y2 - Y1;
        public long Area => (long)Width * Height;

        public Rect Union(Rect other)
        {
            return new Rect(
                Math.Min(X1, other.X1), Math.Min(Y1, other.Y1), Math.Max(X2, other.X2), Math.Max(Y2, other.Y2));
        }

        public bool Contains(Rect other)
        {
            return other.X1 >= X1 && other.Y1 >= Y1 && other.X2 <= X2 && other.Y2 <= Y2;
        }
    }
}
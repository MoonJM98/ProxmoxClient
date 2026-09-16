using System.Windows.Media;

namespace ProxmoxClient.App;

/// <summary>그래프 Y 축 위치.</summary>
public enum GraphAxis
{
    /// <summary>왼쪽 축(CPU·메모리 % 등).</summary>
    Left,

    /// <summary>오른쪽 축(네트워크·디스크 IO 초당 바이트 등).</summary>
    Right
}

/// <summary>통합 그래프의 시계열 하나 — 이름은 범례·팝업·표시 토글 키로 쓰인다.</summary>
public sealed record GraphSeries(
    string Name,
    IReadOnlyList<double?> Values,
    Color Color,
    GraphAxis Axis,
    GraphValueUnit Unit)
{
    /// <summary>값이 하나라도 있는지(노드엔 디스크 IO 가 없는 등) — 없으면 범례에서 제외.</summary>
    public bool HasData => Values.Any(value => value is not null);
}
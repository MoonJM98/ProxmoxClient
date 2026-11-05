using System.Collections.ObjectModel;

namespace ProxmoxClient.UI.ViewModels;

/// <summary>
///     새로 받은 목록을 화면 목록에 맞춰 넣는다 — 같은 키의 항목은 객체를 그대로 두고 값만 바꿔
///     스크롤 위치와 선택이 흔들리지 않게 한다.
/// </summary>
public static class CollectionSync
{
    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source, Func<T, string> key,
        Action<T, T> copy) where T : class
    {
        var existing = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in target) existing.TryAdd(key(item), item);

        var ordered = new List<T>(source.Count);
        foreach (var incoming in source)
            if (existing.TryGetValue(key(incoming), out var current))
            {
                copy(current, incoming);
                ordered.Add(current);
            }
            else
            {
                ordered.Add(incoming);
            }

        Arrange(target, ordered);
    }

    /// <summary>같은 객체들로 순서만 맞춘다(필터 결과 반영 등).</summary>
    public static void Arrange<T>(ObservableCollection<T> target, IReadOnlyList<T> ordered) where T : class
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            if (i < target.Count && ReferenceEquals(target[i], item)) continue;

            var at = IndexOf(target, item, i + 1);
            if (at >= 0)
                target.Move(at, i);
            else
                target.Insert(i, item);
        }

        while (target.Count > ordered.Count) target.RemoveAt(target.Count - 1);
    }

    private static int IndexOf<T>(ObservableCollection<T> list, T item, int start) where T : class
    {
        for (var i = start; i < list.Count; i++)
            if (ReferenceEquals(list[i], item))
                return i;

        return -1;
    }
}

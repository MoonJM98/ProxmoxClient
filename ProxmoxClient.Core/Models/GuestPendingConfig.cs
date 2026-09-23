namespace ProxmoxClient.Core.Models;

/// <summary>
///     게스트 설정과 대기 중 변경(GET …/pending). 실행 중인 게스트는 바로 적용할 수 없는 변경을
///     재시작 때까지 "대기"로 두는데, 웹 UI 는 이를 현재 값 아래 주황색 줄로 보여 준다.
///     pending 응답 한 줄: <c>{ key, value?, pending?, delete? }</c> — delete 1(예정)·2(강제)는 지워질 항목.
/// </summary>
public sealed class GuestPendingConfig
{
    private GuestPendingConfig(IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> effective, IReadOnlySet<string> pendingKeys,
        IReadOnlySet<string> deletedKeys)
    {
        Current = current;
        Effective = effective;
        PendingKeys = pendingKeys;
        DeletedKeys = deletedKeys;
    }

    /// <summary>지금 적용된 값.</summary>
    public IReadOnlyDictionary<string, string> Current { get; }

    /// <summary>대기 중 변경까지 반영한 값 — 편집 창은 이 값으로 연다(웹 UI 의 config 조회와 같다).</summary>
    public IReadOnlyDictionary<string, string> Effective { get; }

    /// <summary>대기 중 변경(값 바뀜 또는 삭제 예정)이 있는 키.</summary>
    public IReadOnlySet<string> PendingKeys { get; }

    /// <summary>삭제 예정인 키.</summary>
    public IReadOnlySet<string> DeletedKeys { get; }

    public static GuestPendingConfig Empty { get; } = FromConfig(new Dictionary<string, string>());

    /// <summary>대기 중 변경이 없는 일반 설정으로 만든다(노드·데이터센터 옵션 등).</summary>
    public static GuestPendingConfig FromConfig(IReadOnlyDictionary<string, string> config)
    {
        return new GuestPendingConfig(config, config, new HashSet<string>(), new HashSet<string>());
    }

    /// <summary>pending 응답 행들로 만든다.</summary>
    public static GuestPendingConfig FromRows(IEnumerable<IReadOnlyDictionary<string, string>> rows)
    {
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        var effective = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var deleted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!row.TryGetValue("key", out var key) || key.Length == 0) continue;

            var hasValue = row.TryGetValue("value", out var value);
            if (hasValue) current[key] = value!;

            var isDeleted = row.TryGetValue("delete", out var delete) && delete is "1" or "2";
            var hasPending = row.TryGetValue("pending", out var newValue);
            if (isDeleted)
            {
                deleted.Add(key);
                pending.Add(key);
            }
            else if (hasPending)
            {
                effective[key] = newValue!;
                if (newValue != value) pending.Add(key);
            }
            else if (hasValue)
            {
                effective[key] = value!;
            }
        }

        return new GuestPendingConfig(current, effective, pending, deleted);
    }

    /// <summary>이 키들 중 하나라도 대기 중 변경이 있는가(부팅 순서처럼 여러 키로 된 줄).</summary>
    public bool HasPending(IEnumerable<string> keys)
    {
        return keys.Any(PendingKeys.Contains);
    }
}

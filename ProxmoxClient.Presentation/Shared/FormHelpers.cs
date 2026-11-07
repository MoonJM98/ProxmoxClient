using ProxmoxClient.Core.Api;

namespace ProxmoxClient.Presentation.Shared;

/// <summary>입력 값·서버 작업 도우미 — 두 앱의 ActionHelpers 가운데 대화상자와 무관한 부분.</summary>
public static class FormHelpers
{
    /// <summary>
    ///     서버 작업을 시작하고 끝날 때까지 기다린 뒤 결과 문구를 만든다. doneKey 의 {0} 에 작업 결과가 들어간다.
    /// </summary>
    public static async Task<string> RunTaskAsync(ProxmoxApiClient api, Task<string> start, string doneKey)
    {
        var upid = await start;
        var status = upid.Length == 0 ? "OK" : (await api.WaitTaskAsync(upid)).Status;
        return UiText.T(doneKey, status);
    }

    /// <summary>
    ///     되돌릴 수 없는 작업용 확인 칸 — 대상 이름을 그대로 입력해야 확인이 눌린다.
    /// </summary>
    public static FormField TypeToConfirmField()
    {
        return new FormField { Key = "confirm", LabelKey = "Action_TypeToConfirm", Required = true };
    }

    public static Func<IReadOnlyDictionary<string, string>, string?> TypedMatches(string expected)
    {
        return values => values.TryGetValue("confirm", out var typed) && typed == expected
            ? null
            : UiText.T("Action_TypeToConfirmMismatch", expected);
    }

    /// <summary>비워 둔 선택 입력은 보내지 않는다 — 서버가 빈 값을 형식 오류로 거절하는 필드가 있다.</summary>
    public static Dictionary<string, string> NonEmpty(IReadOnlyDictionary<string, string> values)
    {
        return values.Where(kv => kv.Value.Length > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    ///     수정용 폼 — 값이 있는 칸은 그대로 보내고, 비운 칸은 "delete" 목록에 넣어 서버 설정에서 지운다.
    /// </summary>
    public static Dictionary<string, string> UpdateForm(IReadOnlyDictionary<string, string> values)
    {
        var form = NonEmpty(values);
        var cleared = values.Where(kv => kv.Value.Length == 0).Select(kv => kv.Key).ToList();
        if (cleared.Count > 0) form["delete"] = string.Join(",", cleared);
        return form;
    }

    /// <summary>행의 값 — 서버가 기본값인 필드는 아예 보내지 않으므로 없으면 빈칸.</summary>
    public static string Value(IReadOnlyDictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var value) ? value : string.Empty;
    }

    public static string Seg(string value)
    {
        return ProxmoxApiClient.PathSegment(value);
    }
}

using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>노드·데이터센터 표 버튼들이 함께 쓰는 입력·삭제 흐름.</summary>
internal static class ActionHelpers
{
    public static TableAction DeleteAction(
        Func<IReadOnlyDictionary<string, string>, string> confirm,
        Func<IReadOnlyDictionary<string, string>, Task<string>> delete,
        string doneKey)
    {
        return new TableAction
        {
            LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
            Confirm = row => confirm(row!),
            Run = async (row, _) =>
            {
                await delete(row!);
                return Loc.T(doneKey);
            }
        };
    }

    /// <summary>입력 대화상자를 띄우고, 확인하면 적용한 뒤 완료 문구를 돌려준다(취소면 null).</summary>
    public static async Task<string?> SubmitAsync(Window? owner, string title, IReadOnlyList<FormField> fields,
        Func<IReadOnlyDictionary<string, string>, Task<string>> apply, string doneKey, bool titleIsKey = true,
        Func<IReadOnlyDictionary<string, string>, string?>? validate = null)
    {
        var dialog = new FormDialog(titleIsKey ? Loc.T(title) : title, fields, validate) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } values) return null;

        await apply(values);
        return Loc.T(doneKey);
    }

    /// <summary>
    ///     입력을 받아 서버 작업(UPID)을 시작하고 끝날 때까지 기다린다. doneKey 의 {0} 에 작업 결과가 들어간다.
    /// </summary>
    public static async Task<string?> SubmitTaskAsync(ProxmoxApiClient api, Window? owner, string title,
        IReadOnlyList<FormField> fields, Func<IReadOnlyDictionary<string, string>, Task<string>> start,
        string doneKey, Func<IReadOnlyDictionary<string, string>, string?>? validate = null)
    {
        var dialog = new FormDialog(title, fields, validate) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } values) return null;

        return await RunTaskAsync(api, start(values), doneKey);
    }

    /// <summary>
    ///     서버 작업을 시작하고 끝날 때까지 기다린 뒤 결과 문구를 만든다. doneKey 의 {0} 에 작업 결과가 들어간다.
    /// </summary>
    public static async Task<string> RunTaskAsync(ProxmoxApiClient api, Task<string> start, string doneKey)
    {
        var upid = await start;
        var status = upid.Length == 0 ? "OK" : (await api.WaitTaskAsync(upid)).Status;
        return Loc.T(doneKey, status);
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
            : Loc.T("Action_TypeToConfirmMismatch", expected);
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

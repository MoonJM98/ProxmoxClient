using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Versioning;

namespace ProxmoxClient.Presentation.Shared;

/// <summary>노드·데이터센터 표 버튼들이 함께 쓰는 입력·삭제 흐름. 화면과 무관한 값 도우미는 <see cref="FormHelpers" />.</summary>
public static class ActionHelpers
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
                return UiText.T(doneKey);
            }
        };
    }

    /// <summary>입력 대화상자를 띄우고, 확인하면 적용한 뒤 완료 문구를 돌려준다(취소면 null).</summary>
    /// <param name="target">입력을 보낼 API 요청 — 서버가 모르는 파라미터 칸은 창에 두지 않는다.</param>
    public static async Task<string?> SubmitAsync(object? owner, string title, IReadOnlyList<FormField> fields,
        Func<IReadOnlyDictionary<string, string>, Task<string>> apply, string doneKey, bool titleIsKey = true,
        Func<IReadOnlyDictionary<string, string>, string?>? validate = null, ApiFeature? target = null)
    {
        var text = titleIsKey ? UiText.T(title) : title;
        if (await AppUi.FormAsync(owner, text, fields, validate, target) is not { } values) return null;

        // 실제 서버 변경 직전에 한 번 더 확인하는 앱(Avalonia 쓰기 확인)은 확인 창에 이 작업 이름을 보인다
        using var flow = AppUi.BeginWrite(text);
        await apply(values);
        return UiText.T(doneKey);
    }

    /// <summary>
    ///     입력을 받아 서버 작업(UPID)을 시작하고 끝날 때까지 기다린다. doneKey 의 {0} 에 작업 결과가 들어간다.
    /// </summary>
    public static async Task<string?> SubmitTaskAsync(ProxmoxApiClient api, object? owner, string title,
        IReadOnlyList<FormField> fields, Func<IReadOnlyDictionary<string, string>, Task<string>> start,
        string doneKey, Func<IReadOnlyDictionary<string, string>, string?>? validate = null,
        ApiFeature? target = null)
    {
        if (await AppUi.FormAsync(owner, title, fields, validate, target) is not { } values) return null;

        using var flow = AppUi.BeginWrite(title);
        return await FormHelpers.RunTaskAsync(api, start(values), doneKey);
    }
}

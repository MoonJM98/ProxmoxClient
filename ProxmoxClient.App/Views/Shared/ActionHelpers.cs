using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Versioning;
using ProxmoxClient.Presentation.Shared;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>노드·데이터센터 표 버튼들이 함께 쓰는 입력·삭제 흐름. 화면과 무관한 값 도우미는 공용 <see cref="FormHelpers" />.</summary>
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
    /// <param name="target">입력을 보낼 API 요청 — 서버가 모르는 파라미터 칸은 창에 두지 않는다.</param>
    public static async Task<string?> SubmitAsync(Window? owner, string title, IReadOnlyList<FormField> fields,
        Func<IReadOnlyDictionary<string, string>, Task<string>> apply, string doneKey, bool titleIsKey = true,
        Func<IReadOnlyDictionary<string, string>, string?>? validate = null, ApiFeature? target = null)
    {
        var dialog = new FormDialog(titleIsKey ? Loc.T(title) : title, fields, validate, target) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } values) return null;

        await apply(values);
        return Loc.T(doneKey);
    }

    /// <summary>
    ///     입력을 받아 서버 작업(UPID)을 시작하고 끝날 때까지 기다린다. doneKey 의 {0} 에 작업 결과가 들어간다.
    /// </summary>
    public static async Task<string?> SubmitTaskAsync(ProxmoxApiClient api, Window? owner, string title,
        IReadOnlyList<FormField> fields, Func<IReadOnlyDictionary<string, string>, Task<string>> start,
        string doneKey, Func<IReadOnlyDictionary<string, string>, string?>? validate = null,
        ApiFeature? target = null)
    {
        var dialog = new FormDialog(title, fields, validate, target) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } values) return null;

        return await FormHelpers.RunTaskAsync(api, start(values), doneKey);
    }
}

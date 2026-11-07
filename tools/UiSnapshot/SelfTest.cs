using System.Runtime.CompilerServices;
using ProxmoxClient.Presentation.Shared;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Shared;

namespace UiSnapshot;

/// <summary>
///     도구 자체 점검용 미리보기(target "preview:selftest") — 옮긴 화면을 등록하는 방법의 예시이기도 하다.
///     실제 기능은 ProxmoxClient.UI/Views/영역/영역Module.cs 의 [ModuleInitializer] 에서 PreviewRegistry.Add 로 등록한다.
/// </summary>
internal static class SelfTest
{
    [ModuleInitializer]
    internal static void Register()
    {
        PreviewRegistry.Add("selftest", new PreviewTab("tasks", "GuestTab_Tasks", "IconList", context =>
            new TableTab(
                () => context.Api.GetTableAsync("cluster/tasks"),
                [
                    new TableColumn { Key = "type", HeaderKey = "Task_Type", Width = 120 },
                    new TableColumn { Key = "id", HeaderKey = "Task_Id", Width = 80 },
                    new TableColumn { Key = "user", HeaderKey = "Task_User", Width = 120 },
                    new TableColumn { Key = "starttime", HeaderKey = "Task_Start", Width = 160, Format = TableFormats.EpochDate },
                    new TableColumn { Key = "status", HeaderKey = "Task_Status", Width = 0 }
                ],
                "DcTasks_Hint",
                [
                    new TableAction
                    {
                        LabelKey = "Action_Add", IconKey = "IconPlus",
                        Run = (_, owner) => ActionHelpers.SubmitAsync(owner, "Action_Add",
                            [
                                new FormField { Key = "name", LabelKey = "Profile_Name", Required = true },
                                new FormField { Key = "enable", LabelKey = "Common_Enabled", Kind = FormFieldKind.Bool, Initial = "1" }
                            ],
                            values => context.Api.PostActionAsync("cluster/selftest", values), "Common_Ok")
                    },
                    ActionHelpers.DeleteAction(row => $"delete {row["upid"]}?",
                        row => context.Api.DeleteActionAsync("cluster/selftest/" + FormHelpers.Seg(row["upid"])),
                        "Common_Ok")
                ],
                filterable: true)));
    }
}

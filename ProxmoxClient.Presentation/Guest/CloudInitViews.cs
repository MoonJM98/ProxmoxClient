using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Presentation.Shared;

namespace ProxmoxClient.Presentation.Guest;

/// <summary>
///     Cloud-Init 확인 — 적용 대기 값(드라이브를 다시 만들어야 반영되는 값, 7.2+)과 서버가 만든 설정 원문(user·network·meta).
/// </summary>
internal static class CloudInitViews
{
    private static readonly IReadOnlyList<TableColumn> PendingColumns =
    [
        new() { Key = "key", HeaderKey = "Table_Name", Width = 140 },
        new() { Key = "value", HeaderKey = "CloudInit_Current", Width = 200 },
        new() { Key = "pending", HeaderKey = "CloudInit_Pending", Width = 200 },
        new() { Key = "delete", HeaderKey = "CloudInit_Deleted", Width = 0, Format = TableFormats.Flag }
    ];

    public static IReadOnlyList<TableAction> Actions(ProxmoxApiClient api, PveResource guest)
    {
        return
        [
            new TableAction
            {
                LabelKey = "CloudInit_ShowPending", IconKey = "IconList",
                Requires = api.Guests.Feature(nameof(GuestsApi.CloudInitPendingAsync)),
                Run = (_, owner) => Task.FromResult(AppUi.ShowView(owner,
                    UiText.T("CloudInit_PendingTitle", guest.VmId),
                    new TableSpec(() => api.Guests.CloudInitPendingAsync(guest.Node, guest.VmId), PendingColumns,
                        "CloudInit_PendingHint")))
            },
            new TableAction
            {
                LabelKey = "CloudInit_ShowDump", IconKey = "IconClipboard",
                Run = (_, owner) =>
                {
                    IReadOnlyList<(string LabelKey, Func<ViewSpec> Create)> dumps =
                    [
                        ("CloudInit_DumpUser", () => Dump(api, guest, "user")),
                        ("CloudInit_DumpNetwork", () => Dump(api, guest, "network")),
                        ("CloudInit_DumpMeta", () => Dump(api, guest, "meta"))
                    ];
                    var tabs = new SubTabsSpec(dumps);
                    return Task.FromResult(AppUi.ShowView(owner, UiText.T("CloudInit_DumpTitle", guest.VmId),
                        tabs));
                }
            }
        ];
    }

    private static TextSpec Dump(ProxmoxApiClient api, PveResource guest, string type)
    {
        return new TextSpec(async () => (await api.Guests.CloudInitDumpAsync(guest.Node, guest.VmId, type),
            string.Empty), null, "CloudInit_DumpHint");
    }
}

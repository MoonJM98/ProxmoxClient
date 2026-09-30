using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest;

/// <summary>
///     게스트 안쪽 동작 — VM 은 QEMU 게스트 에이전트(TRIM·사용자 암호), CT 는 네트워크 인터페이스(8.1+).
///     에이전트가 꺼져 있거나 VM 이 멈춰 있으면 서버 오류가 그대로 보인다.
/// </summary>
internal static class GuestAgentActions
{
    private static readonly IReadOnlyList<TableColumn> InterfaceColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 90 },
        new() { Key = "hwaddr", HeaderKey = "GuestAgent_Mac", Width = 140 },
        new() { Key = "inet", HeaderKey = "GuestAgent_Ipv4", Width = 150 },
        new() { Key = "inet6", HeaderKey = "GuestAgent_Ipv6", Width = 0 }
    ];

    /// <summary>서버가 받는 게스트 암호 최소 길이.</summary>
    private const int MinPasswordLength = 5;

    public static IReadOnlyList<TableAction> Create(ProxmoxApiClient api, PveResource guest,
        PermissionsInfo permissions)
    {
        if (guest.Kind == ResourceKind.Lxc)
            return
            [
                new TableAction
                {
                    LabelKey = "GuestAgent_CtNetwork", IconKey = "IconSwitch",
                    Requires = api.Guests.Feature(nameof(GuestsApi.CtInterfacesAsync)),
                    Run = (_, owner) => Task.FromResult(TableWindow.ShowModal(owner,
                        Loc.T("GuestAgent_CtNetworkTitle", guest.VmId),
                        new TableTab(() => api.Guests.CtInterfacesAsync(guest.Node, guest.VmId), InterfaceColumns,
                            "GuestAgent_CtNetworkHint")))
                }
            ];

        var actions = new List<TableAction>
        {
            new()
            {
                LabelKey = "GuestAgent_Details", IconKey = "IconList",
                Run = (_, owner) => Task.FromResult(TableWindow.ShowModal(owner,
                    Loc.T("GuestAgent_DetailsTitle", guest.VmId), Details(api, guest)))
            },
            new()
            {
                LabelKey = "GuestAgent_Ping", IconKey = "IconCheck",
                Run = async (_, _) =>
                {
                    await api.Guests.AgentPingAsync(guest.Node, guest.VmId);
                    return Loc.T("GuestAgent_PingOk");
                }
            },
            new()
            {
                LabelKey = "GuestAgent_FsTrim", IconKey = "IconDatabase",
                Run = async (_, _) =>
                {
                    var result = await api.Guests.AgentFsTrimAsync(guest.Node, guest.VmId);
                    return Loc.T("GuestAgent_FsTrimDone", Summary(result));
                }
            }
        };

        // 게스트 안 계정 암호 — 콘솔 권한(VM.Console)이 있는 사용자에게만
        if (permissions.CanConsole)
            actions.Add(new TableAction
            {
                LabelKey = "GuestAgent_SetPassword", IconKey = "IconShield",
                Run = (_, owner) => SetPasswordAsync(api, guest, owner)
            });

        actions.AddRange(GuestAgentTools.Create(api, guest, permissions));
        return actions;
    }

    private static async Task<string?> SetPasswordAsync(ProxmoxApiClient api, PveResource guest, Window? owner)
    {
        return await ActionHelpers.SubmitAsync(owner, Loc.T("GuestAgent_SetPasswordTitle", guest.VmId),
        [
            new FormField { Key = "username", LabelKey = "DcUsers_UserName", Required = true, Initial = "root" },
            new FormField { Key = "password", LabelKey = "DcStorage_Password", Kind = FormFieldKind.Password,
                Required = true },
            new FormField { Key = "confirm", LabelKey = "GuestAgent_PasswordConfirm", Kind = FormFieldKind.Password,
                Required = true }
        ], async values =>
        {
            await api.Guests.AgentSetUserPasswordAsync(guest.Node, guest.VmId, values["username"],
                values["password"]);
            return string.Empty;
        }, "GuestAgent_PasswordSet", titleIsKey: false, validate: values =>
            values["password"].Length < MinPasswordLength ? Loc.T("GuestAgent_PasswordShort", MinPasswordLength)
            : values["password"] != values["confirm"] ? Loc.T("GuestAgent_PasswordMismatch")
            : null);
    }

    /// <summary>에이전트 읽기 전용 정보 — 명령마다 하위 탭 하나(열 때 읽는다).</summary>
    private static SubTabsView Details(ProxmoxApiClient api, PveResource guest)
    {
        return new SubTabsView(GuestsApi.AgentInfoCommands
            .Select(command => ($"GuestAgent_{command.Replace("-", "_")}",
                (Func<UIElement>)(() => new TextEditTab(async () =>
                    (await api.Guests.AgentInfoJsonAsync(guest.Node, guest.VmId, command), string.Empty), null,
                    "GuestAgent_DetailsHint"))))
            .ToList());
    }

    /// <summary>에이전트 결과를 한 줄로 — 너무 길면 자른다(자세한 값은 JSON 이라 사람이 읽기 어렵다).</summary>
    private static string Summary(string result)
    {
        const int MaxLength = 200;
        var text = result.ReplaceLineEndings(" ").Trim();
        return text.Length <= MaxLength ? text : text[..MaxLength] + "…";
    }
}

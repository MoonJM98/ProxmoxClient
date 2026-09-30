using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     SDN 적용 전 확인·되돌리기와 IPAM 상태 — 미리 보기(9.1+)는 노드에서 바뀔 내용을, 되돌리기(9.0+)는 적용하지 않은
///     변경을 모두 버린다. IPAM 상태(8.1+)는 PVE IPAM 이 나눠 준 주소 목록.
/// </summary>
internal static class SdnPending
{
    private static readonly IReadOnlyList<TableColumn> IpamColumns =
    [
        new() { Key = "vnet", HeaderKey = "DcSdn_Vnet", Width = 100 },
        new() { Key = "subnet", HeaderKey = "DcSdn_Subnet", Width = 140 },
        new() { Key = "ip", HeaderKey = "Table_Address", Width = 140 },
        new() { Key = "mac", HeaderKey = "GuestAgent_Mac", Width = 140 },
        new() { Key = "hostname", HeaderKey = "DcSdn_Hostname", Width = 120 },
        new() { Key = "vmid", HeaderKey = "Table_Guest", Width = 0 }
    ];

    public static IReadOnlyList<TableAction> Actions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "DcSdn_DryRun", IconKey = "IconSearch",
                Requires = api.Sdn.Feature(nameof(SdnApi.DryRunJsonAsync)),
                Run = async (_, owner) => TextViewWindow.ShowModal(owner, Loc.T("DcSdn_DryRun"),
                    await api.Sdn.DryRunJsonAsync())
            },
            new TableAction
            {
                LabelKey = "DcSdn_Rollback", IconKey = "IconUndo",
                Requires = api.Sdn.Feature(nameof(SdnApi.RollbackAsync)),
                Confirm = _ => Loc.T("DcSdn_RollbackConfirm"),
                Run = async (_, _) => await RunTaskAsync(api, api.Sdn.RollbackAsync(), "DcSdn_RolledBack")
            }
        ];
    }

    /// <summary>VNet 방화벽(8.3+) — 전달 정책과 같은 판에 들어와 그 옵션이 있는 서버에서만 보인다.</summary>
    public static TableAction VnetFirewall(ProxmoxApiClient api)
    {
        return new TableAction
        {
            LabelKey = "GuestTab_Firewall", IconKey = "IconShield", NeedsSelection = true,
            Requires = api.Firewall.Feature(nameof(FirewallApi.UpdateOptionsAsync), "policy_forward"),
            Run = (row, owner) => Task.FromResult(TableWindow.ShowModal(owner,
                Loc.T("DcSdn_VnetFirewallTitle", row!["vnet"]), FirewallTabs.ForVnet(api, row["vnet"], true)))
        };
    }

    /// <summary>IP 매핑 추가·삭제(8.1+) — VNet 의 영역은 VNet 설정에서 찾는다.</summary>
    private static IReadOnlyList<TableAction> IpMappingActions(ProxmoxApiClient api)
    {
        var feature = api.Sdn.Feature(nameof(SdnApi.CreateVnetIpAsync));
        return
        [
            new TableAction
            {
                LabelKey = "DcSdn_IpAdd", IconKey = "IconPlus", Requires = feature,
                Run = async (_, owner) =>
                {
                    var vnets = await api.Sdn.ListAsync("vnets");
                    var choices = vnets.Select(v => (Value(v, "vnet"), $"{Value(v, "vnet")} ({Value(v, "zone")})"))
                        .ToList();
                    return await SubmitAsync(owner, "DcSdn_IpAdd",
                    [
                        new FormField { Key = "vnet", LabelKey = "DcSdn_Vnet", Kind = FormFieldKind.Choice,
                            Choices = choices, Required = true },
                        new FormField { Key = "ip", LabelKey = "Table_Address", Required = true, Trim = true },
                        new FormField { Key = "mac", LabelKey = "GuestAgent_Mac", Trim = true }
                    ], async values =>
                    {
                        var zone = vnets.Where(v => Value(v, "vnet") == values["vnet"]).Select(v => Value(v, "zone"))
                            .FirstOrDefault() ?? string.Empty;
                        if (zone.Length == 0) throw new InvalidOperationException(Loc.T("DcSdn_IpNoZone"));
                        await api.Sdn.CreateVnetIpAsync(values["vnet"], zone, values["ip"], values["mac"]);
                        return string.Empty;
                    }, "DcSdn_IpAdded");
                }
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true, Requires = feature,
                Confirm = row => Loc.T("DcSdn_IpDeleteConfirm", Value(row!, "ip")),
                Run = async (row, _) =>
                {
                    var vnet = Value(row!, "vnet");
                    if (vnet.Length == 0) return Loc.T("DcSdn_IpNoVnet"); // 게이트웨이 등 VNet 이 없는 줄
                    var zone = Value(row!, "zone");
                    if (zone.Length == 0)
                        zone = (await api.Sdn.ListAsync("vnets")).Where(v => Value(v, "vnet") == vnet)
                            .Select(v => Value(v, "zone")).FirstOrDefault() ?? string.Empty;
                    if (zone.Length == 0) return Loc.T("DcSdn_IpNoZone");
                    await api.Sdn.DeleteVnetIpAsync(vnet, zone, Value(row!, "ip"), Value(row!, "mac"));
                    return Loc.T("DcSdn_IpDeleted");
                }
            }
        ];
    }

    public static TableAction IpamStatus(ProxmoxApiClient api, bool canEdit)
    {
        return new TableAction
        {
            LabelKey = "DcSdn_IpamStatus", IconKey = "IconList", NeedsSelection = true,
            Requires = api.Sdn.Feature(nameof(SdnApi.IpamStatusAsync)),
            Run = (row, owner) =>
            {
                var ipam = row!["ipam"];
                if (row.TryGetValue("type", out var type) && type != "pve")
                    return Task.FromResult<string?>(Loc.T("DcSdn_IpamStatusPveOnly"));
                return Task.FromResult(TableWindow.ShowModal(owner, Loc.T("DcSdn_IpamStatusTitle", ipam),
                    new TableTab(() => api.Sdn.IpamStatusAsync(ipam), IpamColumns, "DcSdn_IpamStatusHint",
                        canEdit ? IpMappingActions(api) : null)));
            }
        };
    }
}

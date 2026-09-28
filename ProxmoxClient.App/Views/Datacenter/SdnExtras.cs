using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>SDN 부가 설정 — 라우팅 컨트롤러(EVPN·BGP·IS-IS), IP 관리(IPAM), DNS 연동.</summary>
internal static class SdnExtras
{
    private static readonly IReadOnlyList<TableColumn> ControllerColumns =
    [
        new() { Key = "controller", HeaderKey = "Table_Name", Width = 140 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 70 },
        new() { Key = "asn", HeaderKey = "DcSdn_Asn", Width = 90 },
        new() { Key = "peers", HeaderKey = "DcSdn_Peers", Width = 0 },
        new() { Key = "node", HeaderKey = "Table_Node", Width = 100 },
        new() { Key = "state", HeaderKey = "Table_State", Width = 80 }
    ];

    private static readonly IReadOnlyList<TableColumn> IpamColumns =
    [
        new() { Key = "ipam", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 90 },
        new() { Key = "url", HeaderKey = "StorageDownload_Url", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> DnsColumns =
    [
        new() { Key = "dns", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 90 },
        new() { Key = "url", HeaderKey = "StorageDownload_Url", Width = 0 },
        new() { Key = "ttl", HeaderKey = "DcSdn_Ttl", Width = 70 }
    ];

    public static TableTab Controllers(ProxmoxApiClient api, bool canEdit)
    {
        return new TableTab(() => api.Sdn.ListAsync("controllers", pending: true), ControllerColumns,
            "DcSdn_ControllersHint", canEdit
                ?
                [
                    new TableAction
                    {
                        LabelKey = "Action_Add", IconKey = "IconPlus",
                        Run = (_, owner) => AddControllerAsync(api, owner)
                    },
                    Edit(api, "controllers", "controller", ControllerFields),
                    Delete(api, "controllers", "controller")
                ]
                : null);
    }

    public static TableTab Ipams(ProxmoxApiClient api, bool canEdit)
    {
        return new TableTab(() => api.Sdn.ListAsync("ipams"), IpamColumns, "DcSdn_IpamHint", canEdit
            ?
            [
                new TableAction
                {
                    LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => AddIpamAsync(api, owner)
                },
                Edit(api, "ipams", "ipam", IpamFields),
                Delete(api, "ipams", "ipam"),
                SdnPending.IpamStatus(api, true)
            ]
            : [SdnPending.IpamStatus(api, false)]);
    }

    public static TableTab Dns(ProxmoxApiClient api, bool canEdit)
    {
        return new TableTab(() => api.Sdn.ListAsync("dns"), DnsColumns, "DcSdn_DnsHint", canEdit
            ?
            [
                new TableAction
                {
                    LabelKey = "Action_Add", IconKey = "IconPlus",
                    Run = (_, owner) => SubmitAsync(owner, "DcSdn_AddDns",
                    [
                        new FormField { Key = "dns", LabelKey = "Table_Name", Required = true },
                        new FormField { Key = "url", LabelKey = "StorageDownload_Url", Required = true },
                        new FormField
                        {
                            Key = "key", LabelKey = "DcSdn_ApiKey", Kind = FormFieldKind.Password, Required = true
                        },
                        new FormField { Key = "ttl", LabelKey = "DcSdn_Ttl" }
                    ], values =>
                    {
                        var form = NonEmpty(values);
                        form["type"] = "powerdns";
                        return api.Sdn.CreateAsync("dns", form);
                    }, "DcSdn_Added")
                },
                Edit(api, "dns", "dns", DnsFields),
                Delete(api, "dns", "dns")
            ]
            : null);
    }

    /// <summary>컨트롤러 — 유형마다 필요한 칸만 받는다.</summary>
    private static async Task<string?> AddControllerAsync(ProxmoxApiClient api, Window? owner)
    {
        var choose = new FormDialog(Loc.T("DcSdn_AddController"),
        [
            new FormField
            {
                Key = "type", LabelKey = "Table_Type", Kind = FormFieldKind.Choice, Initial = "evpn",
                Choices = [("evpn", "EVPN"), ("bgp", "BGP"), ("isis", "IS-IS")]
            }
        ]) { Owner = owner };
        if (choose.ShowDialog() != true || choose.Result is not { } picked) return null;

        var type = picked["type"];
        var nodes = (await api.GetNodesAsync()).Select(n => (n.Node, n.Node)).ToList();
        var fields = new List<FormField> { new() { Key = "controller", LabelKey = "Table_Name", Required = true } };
        switch (type)
        {
            case "evpn":
                fields.Add(new FormField { Key = "asn", LabelKey = "DcSdn_Asn", Required = true, Initial = "65000" });
                fields.Add(new FormField { Key = "peers", LabelKey = "DcSdn_Peers", Required = true });
                break;
            case "bgp":
                fields.Add(new FormField
                {
                    Key = "node", LabelKey = "Table_Node", Kind = FormFieldKind.Choice, Choices = nodes, Required = true
                });
                fields.Add(new FormField { Key = "asn", LabelKey = "DcSdn_Asn", Required = true });
                fields.Add(new FormField { Key = "peers", LabelKey = "DcSdn_Peers", Required = true });
                fields.Add(new FormField { Key = "ebgp", LabelKey = "DcSdn_Ebgp", Kind = FormFieldKind.Bool });
                break;
            default:
                fields.Add(new FormField
                {
                    Key = "node", LabelKey = "Table_Node", Kind = FormFieldKind.Choice, Choices = nodes, Required = true
                });
                fields.Add(new FormField { Key = "isis-domain", LabelKey = "DcSdn_IsisDomain", Required = true });
                fields.Add(new FormField { Key = "isis-net", LabelKey = "DcSdn_IsisNet", Required = true });
                fields.Add(new FormField { Key = "isis-ifaces", LabelKey = "DcSdn_IsisIfaces", Required = true });
                break;
        }

        return await SubmitAsync(owner, Loc.T("DcSdn_AddControllerType", type.ToUpperInvariant()), fields, values =>
        {
            var form = NonEmpty(values);
            form["type"] = type;
            return api.Sdn.CreateAsync("controllers", form);
        }, "DcSdn_Added", titleIsKey: false);
    }

    private static async Task<string?> AddIpamAsync(ProxmoxApiClient api, Window? owner)
    {
        var choose = new FormDialog(Loc.T("DcSdn_AddIpam"),
        [
            new FormField
            {
                Key = "type", LabelKey = "Table_Type", Kind = FormFieldKind.Choice, Initial = "netbox",
                Choices = [("netbox", "NetBox"), ("phpipam", "phpIPAM"), ("pve", "PVE")]
            }
        ]) { Owner = owner };
        if (choose.ShowDialog() != true || choose.Result is not { } picked) return null;

        var type = picked["type"];
        var fields = new List<FormField> { new() { Key = "ipam", LabelKey = "Table_Name", Required = true } };
        if (type != "pve")
        {
            fields.Add(new FormField { Key = "url", LabelKey = "StorageDownload_Url", Required = true });
            fields.Add(new FormField
            {
                Key = "token", LabelKey = "DcMetrics_Token", Kind = FormFieldKind.Password, Required = true
            });
        }

        if (type == "phpipam")
            fields.Add(new FormField { Key = "section", LabelKey = "DcSdn_Section", Required = true });

        return await SubmitAsync(owner, Loc.T("DcSdn_AddIpam"), fields, values =>
        {
            var form = NonEmpty(values);
            form["type"] = type;
            return api.Sdn.CreateAsync("ipams", form);
        }, "DcSdn_Added", titleIsKey: false);
    }

    private static TableAction Delete(ProxmoxApiClient api, string collection, string idKey)
    {
        return DeleteAction(row => Loc.T("DcSdn_DeleteConfirm", row[idKey]),
            row => api.Sdn.DeleteAsync(collection, row[idKey]), "DcSdn_Deleted");
    }

    /// <summary>
    ///     수정 — 서버 설정을 읽어 유형별 칸을 채운다(이름·유형은 바꿀 수 없다). 비밀(토큰·키)은 비우면 그대로 둔다.
    /// </summary>
    private static TableAction Edit(ProxmoxApiClient api, string collection, string idKey,
        Func<string, IReadOnlyDictionary<string, string>, List<FormField>> fields)
    {
        return new TableAction
        {
            LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
            Run = async (row, owner) =>
            {
                var id = row![idKey];
                var config = await api.Sdn.GetAsync(collection, id);
                var type = config.TryGetValue("type", out var t) ? t : string.Empty;
                return await SubmitAsync(owner, Loc.T("DcSdn_EditTitle", row[idKey]), fields(type, config), values =>
                {
                    var form = values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                    foreach (var secret in new[] { "token", "key" })
                        if (form.TryGetValue(secret, out var v) && v.Length == 0) form.Remove(secret);
                    return api.Sdn.UpdateAsync(collection, id, UpdateForm(form));
                }, "DcSdn_Updated", titleIsKey: false);
            }
        };
    }

    private static string C(IReadOnlyDictionary<string, string> config, string key)
    {
        return config.TryGetValue(key, out var v) ? v : string.Empty;
    }

    private static List<FormField> ControllerFields(string type, IReadOnlyDictionary<string, string> c)
    {
        return type switch
        {
            "evpn" =>
            [
                new FormField { Key = "asn", LabelKey = "DcSdn_Asn", Required = true, Initial = C(c, "asn") },
                new FormField { Key = "peers", LabelKey = "DcSdn_Peers", Required = true, Initial = C(c, "peers") }
            ],
            "bgp" =>
            [
                new FormField { Key = "asn", LabelKey = "DcSdn_Asn", Required = true, Initial = C(c, "asn") },
                new FormField { Key = "peers", LabelKey = "DcSdn_Peers", Required = true, Initial = C(c, "peers") },
                new FormField { Key = "ebgp", LabelKey = "DcSdn_Ebgp", Kind = FormFieldKind.Bool,
                    Initial = C(c, "ebgp") is "1" ? "1" : "0" },
                new FormField { Key = "loopback", LabelKey = "DcSdn_Loopback", Initial = C(c, "loopback"),
                    Advanced = true },
                new FormField { Key = "ebgp-multihop", LabelKey = "DcSdn_EbgpMultihop",
                    Initial = C(c, "ebgp-multihop"), Advanced = true },
                new FormField { Key = "bgp-multipath-as-path-relax", LabelKey = "DcSdn_MultipathRelax",
                    Kind = FormFieldKind.Bool, Initial = C(c, "bgp-multipath-as-path-relax") is "1" ? "1" : "0",
                    Advanced = true }
            ],
            _ =>
            [
                new FormField { Key = "isis-domain", LabelKey = "DcSdn_IsisDomain", Required = true,
                    Initial = C(c, "isis-domain") },
                new FormField { Key = "isis-net", LabelKey = "DcSdn_IsisNet", Required = true,
                    Initial = C(c, "isis-net") },
                new FormField { Key = "isis-ifaces", LabelKey = "DcSdn_IsisIfaces", Required = true,
                    Initial = C(c, "isis-ifaces") },
                new FormField { Key = "loopback", LabelKey = "DcSdn_Loopback", Initial = C(c, "loopback"),
                    Advanced = true }
            ]
        };
    }

    private static List<FormField> IpamFields(string type, IReadOnlyDictionary<string, string> c)
    {
        if (type == "pve") return [];
        var fields = new List<FormField>
        {
            new() { Key = "url", LabelKey = "StorageDownload_Url", Required = true, Initial = C(c, "url") },
            new() { Key = "token", LabelKey = "DcMetrics_Token", Kind = FormFieldKind.Password,
                Hint = Loc.T("StorageHint_KeepSecret") },
            new() { Key = "fingerprint", LabelKey = "Table_Fingerprint", Initial = C(c, "fingerprint"),
                Advanced = true }
        };
        if (type == "phpipam")
            fields.Add(new FormField { Key = "section", LabelKey = "DcSdn_Section", Required = true,
                Initial = C(c, "section") });
        return fields;
    }

    private static List<FormField> DnsFields(string type, IReadOnlyDictionary<string, string> c)
    {
        return
        [
            new FormField { Key = "url", LabelKey = "StorageDownload_Url", Required = true, Initial = C(c, "url") },
            new FormField { Key = "key", LabelKey = "DcSdn_ApiKey", Kind = FormFieldKind.Password,
                Hint = Loc.T("StorageHint_KeepSecret") },
            new FormField { Key = "ttl", LabelKey = "DcSdn_Ttl", Initial = C(c, "ttl") },
            new FormField { Key = "fingerprint", LabelKey = "Table_Fingerprint", Initial = C(c, "fingerprint"),
                Advanced = true }
        ];
    }
}

using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     SDN 영역·VNet·서브넷 추가·수정(웹 UI sdn/zones/*, VnetEdit, SubnetEdit). 수정은 서버 설정을 읽어 채우고
///     비운 칸은 delete 로 기본값에 돌린다. 적용(Apply)하기 전까지는 대기 중 변경으로 남는다.
/// </summary>
internal static partial class SdnActions
{

    [GeneratedRegex("^[a-z][a-z0-9]{0,6}[a-z0-9]$")]
    private static partial Regex SdnIdPattern();

    /// <summary>영역·VNet ID — 영문으로 시작하는 영문·숫자 8자 이내(서버 규칙).</summary>
    internal static string? IdProblem(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var id) && !SdnIdPattern().IsMatch(id) ? Loc.T("DcSdn_ZoneIdHint") : null;
    }

    public static readonly IReadOnlyList<TableColumn> SubnetColumns =
    [
        new() { Key = "cidr", HeaderKey = "Table_Cidr", Width = 160 },
        new() { Key = "gateway", HeaderKey = "Table_Gateway", Width = 130 },
        new() { Key = "snat", HeaderKey = "DcSdn_Snat", Width = 60, Format = TableFormats.Flag },
        new() { Key = "dhcp-range", HeaderKey = "DcSdn_DhcpRange", Width = 200 },
        new() { Key = "state", HeaderKey = "Table_State", Width = 0 }
    ];

    // ------------------------------------------------------------ 영역

    public static IReadOnlyList<TableAction> Zones(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => AddZoneAsync(api, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditZoneAsync(api, row!["zone"], owner)
            },
            DeleteAction(row => Loc.T("DcSdn_DeleteConfirm", row["zone"]),
                row => api.Sdn.DeleteAsync("zones", row["zone"]), "DcSdn_Deleted")
        ];
    }

    private static async Task<string?> AddZoneAsync(ProxmoxApiClient api, Window? owner)
    {
        var choose = new FormDialog(Loc.T("DcSdn_AddZone"),
        [
            new FormField
            {
                Key = "type", LabelKey = "Table_Type", Kind = FormFieldKind.Choice, Initial = "simple",
                Choices =
                [
                    ("simple", "Simple"), ("vlan", "VLAN"), ("qinq", "QinQ"), ("vxlan", "VXLAN"),
                    ("evpn", "EVPN")
                ]
            }
        ]) { Owner = owner };
        if (choose.ShowDialog() != true || choose.Result is not { } picked) return null;

        var type = picked["type"];
        var fields = await ZoneFieldsAsync(api, type, null);
        return await SubmitAsync(owner, Loc.T("DcSdn_AddZoneType", type), fields, values =>
        {
            var form = NonEmpty(values);
            form["type"] = type;
            return api.Sdn.CreateAsync("zones", form);
        }, "DcSdn_Added", titleIsKey: false, validate: v => IdProblem(v, "zone"));
    }

    private static async Task<string?> EditZoneAsync(ProxmoxApiClient api, string zone, Window? owner)
    {
        var config = await api.Sdn.GetAsync("zones", zone);
        var type = config.TryGetValue("type", out var t) ? t : "simple";
        var fields = await ZoneFieldsAsync(api, type, config);
        return await SubmitAsync(owner, Loc.T("DcSdn_EditTitle", zone), fields,
            values => api.Sdn.UpdateAsync("zones", zone, UpdateForm(values)), "DcSdn_Updated",
            titleIsKey: false);
    }

    /// <summary>config 가 null 이면 추가(이름 칸 포함). 유형은 만든 뒤 바꿀 수 없다.</summary>
    private static async Task<List<FormField>> ZoneFieldsAsync(ProxmoxApiClient api, string type,
        IReadOnlyDictionary<string, string>? config)
    {
        string V(string key, string fallback = "") =>
            config is null ? fallback : config.TryGetValue(key, out var v) ? v : string.Empty;
        FormField Text(string key, string labelKey, bool required = false, string? hint = null, bool adv = false) =>
            new() { Key = key, LabelKey = labelKey, Required = required, Initial = V(key), Trim = true,
                Hint = hint is null ? null : Loc.T(hint), Advanced = adv };
        FormField Flag(string key, string labelKey, bool adv = true) =>
            new() { Key = key, LabelKey = labelKey, Kind = FormFieldKind.Bool, Initial = V(key) is "1" ? "1" : "0",
                Advanced = adv };

        var nodes = (await api.GetNodesAsync()).Select(n => (n.Node, n.Node)).OrderBy(n => n.Item1).ToList();
        var fields = new List<FormField>();
        if (config is null) fields.Add(Text("zone", "Table_Name", true, "DcSdn_ZoneIdHint"));

        switch (type)
        {
            case "simple":
                fields.Add(new FormField { Key = "dhcp", LabelKey = "DcSdn_AutoDhcp", Kind = FormFieldKind.Choice,
                    Initial = V("dhcp"), Choices = [("", "DcHa_NoGroup"), ("dnsmasq", "dnsmasq")], Advanced = true });
                break;
            case "vlan":
                fields.Add(new FormField { Key = "bridge", LabelKey = "DcSdn_Bridge", Required = true,
                    Initial = V("bridge", "vmbr0"), Trim = true });
                break;
            case "qinq":
                fields.Add(new FormField { Key = "bridge", LabelKey = "DcSdn_Bridge", Required = true,
                    Initial = V("bridge", "vmbr0"), Trim = true });
                fields.Add(Text("tag", "DcSdn_ServiceVlan", true));
                fields.Add(new FormField { Key = "vlan-protocol", LabelKey = "DcSdn_VlanProtocol",
                    Kind = FormFieldKind.Choice, Initial = V("vlan-protocol"),
                    Choices = [("", "802.1q"), ("802.1ad", "802.1ad")] });
                break;
            case "vxlan":
                fields.Add(Text("peers", "DcSdn_Peers", true, "DcSdn_PeersHint"));
                break;
            case "evpn":
                var controllers = (await api.Sdn.ListAsync("controllers"))
                    .Where(c => Value(c, "type") == "evpn")
                    .Select(c => (Value(c, "controller"), Value(c, "controller"))).ToList();
                fields.Add(new FormField { Key = "controller", LabelKey = "DcSdn_Controller",
                    Kind = FormFieldKind.Choice, Choices = controllers, Required = true, Initial = V("controller") });
                fields.Add(Text("vrf-vxlan", "DcSdn_VrfVxlan", true));
                fields.Add(new FormField { Key = "exitnodes", LabelKey = "DcSdn_ExitNodes",
                    Kind = FormFieldKind.MultiChoice, Choices = nodes, Initial = V("exitnodes") });
                fields.Add(new FormField { Key = "exitnodes-primary", LabelKey = "DcSdn_ExitPrimary",
                    Kind = FormFieldKind.Choice, Choices = [("", "DcHa_NoGroup"), ..nodes],
                    Initial = V("exitnodes-primary"), Advanced = true });
                fields.Add(Flag("exitnodes-local-routing", "DcSdn_ExitLocalRouting"));
                fields.Add(Flag("advertise-subnets", "DcSdn_AdvertiseSubnets"));
                fields.Add(Flag("disable-arp-nd-suppression", "DcSdn_NoArpSuppression"));
                fields.Add(Text("mac", "DcSdn_VnetMac", adv: true));
                fields.Add(Text("rt-import", "DcSdn_RtImport", adv: true));
                break;
        }

        fields.Add(new FormField { Key = "nodes", LabelKey = "DcStorage_Nodes", Kind = FormFieldKind.MultiChoice,
            Choices = nodes, Initial = V("nodes"), Hint = Loc.T("StorageHint_Nodes") });
        fields.Add(Text("mtu", "NodeNetwork_Mtu", hint: "DcSdn_MtuHint"));
        fields.AddRange(await DnsFieldsAsync(api, V));
        return fields;
    }

    /// <summary>IPAM·DNS 연동 칸 — 목록은 SDN 설정에서 읽는다(못 읽으면 기본값만).</summary>
    private static async Task<IEnumerable<FormField>> DnsFieldsAsync(ProxmoxApiClient api,
        Func<string, string, string> value)
    {
        async Task<List<(string, string)>> Names(string collection, string key)
        {
            try
            {
                return (await api.Sdn.ListAsync(collection)).Select(r => (Value(r, key), Value(r, key))).ToList();
            }
            catch (ProxmoxApiException)
            {
                return [];
            }
        }

        var ipams = await Names("ipams", "ipam");
        var dns = await Names("dns", "dns");
        return
        [
            new FormField { Key = "ipam", LabelKey = "DcSdn_Ipam", Kind = FormFieldKind.Choice,
                Choices = [("", "DcHa_NoGroup"), ..ipams], Initial = value("ipam", "pve") },
            new FormField { Key = "dns", LabelKey = "DcSdn_Dns", Kind = FormFieldKind.Choice,
                Choices = [("", "DcHa_NoGroup"), ..dns], Initial = value("dns", ""), Advanced = true },
            new FormField { Key = "reversedns", LabelKey = "DcSdn_ReverseDns", Kind = FormFieldKind.Choice,
                Choices = [("", "DcHa_NoGroup"), ..dns], Initial = value("reversedns", ""), Advanced = true },
            new FormField { Key = "dnszone", LabelKey = "DcSdn_DnsZone", Initial = value("dnszone", ""), Trim = true,
                Advanced = true }
        ];
    }

    // ------------------------------------------------------------ VNet

    public static IReadOnlyList<TableAction> Vnets(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => EditVnetAsync(api, null, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditVnetAsync(api, row!["vnet"], owner)
            },
            new TableAction
            {
                LabelKey = "DcSdn_Subnets", IconKey = "IconList", NeedsSelection = true,
                Run = (row, owner) =>
                {
                    var vnet = row!["vnet"];
                    return Task.FromResult(TableWindow.ShowModal(owner, Loc.T("DcSdn_SubnetsTitle", vnet),
                        new TableTab(() => api.Sdn.ListSubnetsAsync(vnet, pending: true), SubnetColumns,
                            "DcSdn_SubnetsHint", Subnets(api, vnet))));
                }
            },
            DeleteAction(row => Loc.T("DcSdn_DeleteConfirm", row["vnet"]),
                row => api.Sdn.DeleteAsync("vnets", row["vnet"]), "DcSdn_Deleted")
        ];
    }

    private static async Task<string?> EditVnetAsync(ProxmoxApiClient api, string? vnet, Window? owner)
    {
        var config = vnet is null ? null : await api.Sdn.GetAsync("vnets", vnet);
        string V(string key) => config is not null && config.TryGetValue(key, out var v) ? v : string.Empty;
        var zones = (await api.Sdn.ListAsync("zones")).Select(z => (Value(z, "zone"), Value(z, "zone"))).ToList();

        var fields = new List<FormField>();
        if (vnet is null)
            fields.Add(new FormField { Key = "vnet", LabelKey = "Table_Name", Required = true, Trim = true,
                Hint = Loc.T("DcSdn_VnetIdHint") });
        fields.AddRange(
        [
            new FormField { Key = "zone", LabelKey = "DcSdn_Zone", Kind = FormFieldKind.Choice, Choices = zones,
                Required = true, Initial = V("zone") },
            new FormField { Key = "alias", LabelKey = "DcSdn_Alias", Initial = V("alias") },
            new FormField { Key = "tag", LabelKey = "DcSdn_Tag", Initial = V("tag"), Trim = true,
                Hint = Loc.T("DcSdn_TagHint") },
            new FormField { Key = "vlanaware", LabelKey = "NodeNetwork_VlanAware", Kind = FormFieldKind.Bool,
                Initial = V("vlanaware") is "1" ? "1" : "0" },
            new FormField { Key = "isolate-ports", LabelKey = "DcSdn_IsolatePorts", Kind = FormFieldKind.Bool,
                Initial = V("isolate-ports") is "1" ? "1" : "0", Advanced = true }
        ]);

        return await SubmitAsync(owner, vnet is null ? Loc.T("DcSdn_AddVnet") : Loc.T("DcSdn_EditTitle", vnet),
            fields, values => vnet is null
                ? api.Sdn.CreateAsync("vnets", NonEmpty(values))
                : api.Sdn.UpdateAsync("vnets", vnet, UpdateForm(values)),
            vnet is null ? "DcSdn_Added" : "DcSdn_Updated", titleIsKey: false,
            validate: v => IdProblem(v, "vnet"));
    }

    // ------------------------------------------------------------ 서브넷

    private static IReadOnlyList<TableAction> Subnets(ProxmoxApiClient api, string vnet)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => SubmitAsync(owner, "DcSdn_AddSubnet", SubnetFields(null),
                    values => api.Sdn.CreateSubnetAsync(vnet, SubnetPairs(values, true)), "DcSdn_Added",
                    validate: ValidateSubnet)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = async (row, owner) =>
                {
                    // 목록(pending=1)의 윗단 값은 적용 전 옛 값일 수 있다 — 서브넷 설정을 다시 읽는다
                    var id = Value(row!, "subnet");
                    var config = await api.Sdn.GetSubnetAsync(vnet, id);
                    return await SubmitAsync(owner, Loc.T("DcSdn_EditTitle", Value(row!, "cidr")),
                        SubnetFields(config), values => api.Sdn.UpdateSubnetAsync(vnet, id, SubnetPairs(values, false)),
                        "DcSdn_Updated", titleIsKey: false, validate: ValidateSubnet);
                }
            },
            // 서브넷 ID 는 "영역-주소-접두어" 형식이라 행의 subnet(ID) 필드로 지운다
            DeleteAction(row => Loc.T("DcSdn_DeleteConfirm", Value(row, "cidr")),
                row => api.Sdn.DeleteSubnetAsync(vnet, Value(row, "subnet")), "DcSdn_Deleted")
        ];
    }

    /// <summary><paramref name="config" /> 가 null 이면 추가. DHCP 범위는 여러 개 — 줄마다 "시작 - 끝".</summary>
    private static List<FormField> SubnetFields(IReadOnlyDictionary<string, string>? config)
    {
        string V(string key) => config is not null && config.TryGetValue(key, out var v) ? v : string.Empty;
        var fields = new List<FormField>();
        if (config is null)
            fields.Add(new FormField { Key = "subnet", LabelKey = "Table_Cidr", Required = true, Trim = true,
                Hint = "10.0.0.0/24" });
        fields.AddRange(
        [
            new FormField { Key = "gateway", LabelKey = "Table_Gateway", Initial = V("gateway"), Trim = true },
            new FormField { Key = "snat", LabelKey = "DcSdn_Snat", Kind = FormFieldKind.Bool,
                Initial = V("snat") is "1" ? "1" : "0" },
            new FormField { Key = "dhcp-ranges", LabelKey = "DcSdn_DhcpRange", Kind = FormFieldKind.Multiline,
                Initial = string.Join('\n', DhcpRanges(V("dhcp-range")).Select(r => $"{r.Start} - {r.End}")),
                Hint = Loc.T("DcSdn_DhcpHint") },
            new FormField { Key = "dhcp-dns-server", LabelKey = "DcSdn_DhcpDns", Initial = V("dhcp-dns-server"),
                Trim = true, Advanced = true },
            new FormField { Key = "dnszoneprefix", LabelKey = "DcSdn_DnsZonePrefix", Initial = V("dnszoneprefix"),
                Trim = true, Advanced = true }
        ]);
        return fields;
    }

    /// <summary>서버의 dhcp-range 배열(객체 또는 "start-address=…,end-address=…" 줄들) → (시작, 끝) 목록.</summary>
    internal static IEnumerable<(string Start, string End)> DhcpRanges(string lines)
    {
        foreach (var line in lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = PropertyString.Parse(PropertyString.FromJsonObject(line));
            if (p.Get("start-address") is { Length: > 0 } start && p.Get("end-address") is { Length: > 0 } end)
                yield return (start, end);
        }
    }

    /// <summary>"시작 - 끝" 한 줄 → (시작, 끝). 형식이 틀리면 null.</summary>
    private static (string Start, string End)? ParseRange(string line)
    {
        var parts = line.Split(" - ", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) parts = line.Split('-', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0 ? (parts[0], parts[1]) : null;
    }

    /// <summary>
    ///     서브넷 요청 값 — DHCP 범위는 줄마다 dhcp-range 를 반복한다(배열). 수정 때 비운 칸은 delete 로.
    /// </summary>
    internal static List<KeyValuePair<string, string>> SubnetPairs(IReadOnlyDictionary<string, string> values,
        bool isCreate)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        var cleared = new List<string>();
        if (isCreate)
        {
            pairs.Add(new("subnet", values["subnet"]));
            pairs.Add(new("type", "subnet"));
        }

        foreach (var key in new[] { "gateway", "dhcp-dns-server", "dnszoneprefix" })
            if (values[key].Length > 0) pairs.Add(new(key, values[key]));
            else cleared.Add(key);
        if (values["snat"] == "1") pairs.Add(new("snat", "1"));
        else cleared.Add("snat");

        var ranges = values["dhcp-ranges"]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseRange).OfType<(string Start, string End)>().ToList();
        pairs.AddRange(ranges.Select(r =>
            new KeyValuePair<string, string>("dhcp-range", $"start-address={r.Start},end-address={r.End}")));
        if (ranges.Count == 0) cleared.Add("dhcp-range");

        if (!isCreate && cleared.Count > 0) pairs.Add(new("delete", string.Join(',', cleared)));
        return pairs;
    }

    private static string? ValidateSubnet(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("subnet", out var cidr) && !cidr.Contains('/')) return Loc.T("Wz_NeedCidr");
        return values["dhcp-ranges"].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(line => ParseRange(line) is not null)
            ? null
            : Loc.T("DcSdn_DhcpBoth");
    }
}

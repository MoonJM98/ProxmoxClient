using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>
///     CT 네트워크 탭(웹 UI lxc/Network.js) — 장치 목록과 추가·편집·제거.
///     저장 순서: name, hwaddr, bridge, tag, firewall, ip, gw, ip6, gw6, link_down, mtu, rate — 화면에 없는 키는 유지.
/// </summary>
internal static partial class CtNetwork
{
    private const int MaxNet = 32;

    private static readonly string[] FormKeys =
        ["name", "hwaddr", "bridge", "tag", "firewall", "ip", "gw", "ip6", "gw6", "link_down", "mtu", "rate"];

    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new() { Key = "id", HeaderKey = "Table_Id", Width = 60 },
        new() { Key = "name", HeaderKey = "Table_Name", Width = 70 },
        new() { Key = "bridge", HeaderKey = "GuestSettingsWindow_17", Width = 80 },
        new() { Key = "firewall", HeaderKey = "FirewallWindow_01", Width = 70, Format = TableFormats.Flag },
        new() { Key = "tag", HeaderKey = "GuestSettingsWindow_21", Width = 60 },
        new() { Key = "hwaddr", HeaderKey = "GuestSettingsWindow_19", Width = 130 },
        new() { Key = "address", HeaderKey = "Table_Address", Width = 0 },
        new() { Key = "gateway", HeaderKey = "CtNet_Gateway", Width = 130 },
        new() { Key = "mtu", HeaderKey = "HwEd_Mtu", Width = 60 },
        new() { Key = "link_down", HeaderKey = "HwEd_Disconnect", Width = 70, Format = TableFormats.Flag }
    ];

    [GeneratedRegex("^([a-fA-F0-9]{2}:){5}[a-fA-F0-9]{2}$")]
    private static partial Regex MacPattern();

    [GeneratedRegex(@"^net(\d+)$")]
    private static partial Regex NetKey();

    private static string V(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
    }

    public static TableTab Create(ProxmoxApiClient api, PveResource guest)
    {
        return new TableTab(async () => ToRows(await api.GetGuestConfigAsync(guest.Node, guest.Kind, guest.VmId)),
            Columns, "CtNet_Hint",
        [
            new TableAction { LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => EditAsync(api, guest, null, owner) },
            new TableAction { LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditAsync(api, guest, row!["id"], owner) },
            new TableAction
            {
                LabelKey = "Hw_Remove", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => Loc.T("Hw_RemoveConfirm", row!["id"]),
                Run = async (row, _) =>
                {
                    await api.UpdateGuestConfigAsync(guest.Node, guest.Kind, guest.VmId,
                        new Dictionary<string, string> { ["delete"] = row!["id"] });
                    return Loc.T("Hw_Removed");
                }
            }
        ]);
    }

    /// <summary>netN 설정들을 표 행으로 — 주소·게이트웨이는 IPv4·IPv6 를 함께 보인다.</summary>
    internal static IReadOnlyList<IReadOnlyDictionary<string, string>> ToRows(
        IReadOnlyDictionary<string, string> config)
    {
        return config.Where(kv => NetKey().IsMatch(kv.Key))
            .OrderBy(kv => int.Parse(NetKey().Match(kv.Key).Groups[1].Value, CultureInfo.InvariantCulture))
            .Select(kv =>
            {
                var net = PropertyString.Parse(kv.Value);
                var row = new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = kv.Key };
                foreach (var key in new[] { "name", "bridge", "firewall", "tag", "hwaddr", "mtu", "link_down" })
                    row[key] = net.Get(key);
                row["address"] = string.Join(", ",
                    new[] { Addr("ip", net), Addr("ip6", net) }.Where(a => a.Length > 0));
                row["gateway"] = string.Join(", ", new[] { net.Get("gw"), net.Get("gw6") }.Where(a => a.Length > 0));
                return (IReadOnlyDictionary<string, string>)row;
            }).ToList();
    }

    private static string Addr(string key, PropertyString net)
    {
        var value = net.Get(key);
        return value is "dhcp" or "auto" ? $"{key}: {value}" : value;
    }

    private static async Task<string?> EditAsync(ProxmoxApiClient api, PveResource guest, string? key, Window? owner)
    {
        var config = await api.GetGuestPendingAsync(guest.Node, guest.Kind, guest.VmId);
        var ctx = new HardwareContext(api, guest, config, running: false);
        var edit = await EditorAsync(ctx, key);
        var dialog = new FormDialog(edit.Title, edit.Fields, edit.Validate) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } values) return null;

        await api.UpdateGuestConfigAsync(guest.Node, guest.Kind, guest.VmId,
            ActionHelpers.UpdateForm(edit.Build(values)));
        return Loc.T("GuestSettingsWindow_M06");
    }

    /// <summary>CT 네트워크 편집 창 — 새로 만들면 이름 eth0/eth1…, 방화벽 켬, 첫 브리지.</summary>
    internal static async Task<HardwareEdit> EditorAsync(HardwareContext ctx, string? key)
    {
        var bridges = await ctx.BridgeChoicesAsync();
        var target = key ?? ctx.FreeSlot("net", MaxNet);
        var net = key is null ? PropertyString.Empty : PropertyString.Parse(ctx.Get(key));
        var names = ctx.Effective.Where(kv => NetKey().IsMatch(kv.Key) && kv.Key != key)
            .Select(kv => PropertyString.Parse(kv.Value).Get("name")).ToHashSet(StringComparer.Ordinal);
        var defaultName = Enumerable.Range(0, MaxNet).Select(i => $"eth{i}").First(n => !names.Contains(n));
        var v4 = net.Get("ip") == "dhcp" ? "dhcp" : "static";
        var v6 = net.Get("ip6") is "dhcp" or "auto" ? net.Get("ip6") : "static";
        return new HardwareEdit
        {
            Title = key is null ? Loc.T("CtNet_Add") : Loc.T("CtNet_Edit", key),
            Fields =
            [
                new FormField { Key = "name", LabelKey = "Table_Name", Required = true,
                    Initial = key is null ? defaultName : net.Get("name") },
                new FormField { Key = "hwaddr", LabelKey = "GuestSettingsWindow_19", Initial = net.Get("hwaddr"),
                    Hint = Loc.T("HwEd_MacAuto") },
                new FormField { Key = "bridge", LabelKey = "GuestSettingsWindow_17", Kind = FormFieldKind.Choice,
                    Required = true, Choices = bridges,
                    Initial = key is null ? bridges.FirstOrDefault().Value ?? "" : net.Get("bridge") },
                new FormField { Key = "tag", LabelKey = "GuestSettingsWindow_21", Initial = net.Get("tag"),
                    Hint = Loc.T("HwEd_NoVlan") },
                new FormField { Key = "firewall", LabelKey = "FirewallWindow_01", Kind = FormFieldKind.Bool,
                    Initial = key is null || net.IsOn("firewall") ? "1" : "0" },
                new FormField { Key = "v4", LabelKey = "IpConfig_V4Mode", Kind = FormFieldKind.Choice, Initial = v4,
                    Choices = [("static", "IpConfig_Static"), ("dhcp", "DHCP")] },
                new FormField { Key = "ip", LabelKey = "IpConfig_V4Cidr", Initial = v4 == "static" ? net.Get("ip") : "",
                    Hint = Loc.T("IpConfig_StaticOnly") },
                new FormField { Key = "gw", LabelKey = "IpConfig_V4Gateway", Initial = net.Get("gw") },
                new FormField { Key = "v6", LabelKey = "IpConfig_V6Mode", Kind = FormFieldKind.Choice, Initial = v6,
                    Choices = [("static", "IpConfig_Static"), ("dhcp", "DHCP"), ("auto", "SLAAC")] },
                new FormField { Key = "ip6", LabelKey = "IpConfig_V6Cidr",
                    Initial = v6 == "static" ? net.Get("ip6") : "",
                    Hint = Loc.T("IpConfig_StaticOnly") },
                new FormField { Key = "gw6", LabelKey = "IpConfig_V6Gateway", Initial = net.Get("gw6") },
                new FormField { Key = "link_down", LabelKey = "HwEd_Disconnect", Kind = FormFieldKind.Bool,
                    Advanced = true, Initial = net.IsOn("link_down") ? "1" : "0" },
                new FormField { Key = "mtu", LabelKey = "HwEd_Mtu", Advanced = true, Initial = net.Get("mtu"),
                    Hint = Loc.T("CtNet_MtuHint") },
                new FormField { Key = "rate", LabelKey = "HwEd_RateLimit", Advanced = true, Initial = net.Get("rate"),
                    Hint = Loc.T("HwEd_UnlimitedHint") }
            ],
            Validate = values => target is null ? Loc.T("HwEd_NoFreeSlot", "net") : Validate(values, names),
            Build = values => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [target!] = FormatNetwork(values, net)
            }
        };
    }

    private static string? Validate(IReadOnlyDictionary<string, string> values, IReadOnlySet<string> otherNames)
    {
        if (otherNames.Contains(V(values, "name"))) return Loc.T("CtNet_NameInUse");
        if (V(values, "hwaddr") is { Length: > 0 } mac && !MacPattern().IsMatch(mac)) return Loc.T("HwEd_MacInvalid");
        if (V(values, "tag") is { Length: > 0 } tag && !(int.TryParse(tag, out var t) && t is >= 1 and <= 4094))
            return Loc.T("GuestSettings_VlanRange");
        if (V(values, "mtu") is { Length: > 0 } mtu && !(int.TryParse(mtu, out var m) && m is >= 576 and <= 65535))
            return Loc.T("CtNet_MtuRange");
        return V(values, "rate") is { Length: > 0 } rate
               && !(double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
                    && r is >= 0 and <= 10240)
            ? Loc.T("HwEd_RateRange")
            : null;
    }

    /// <summary>화면 칸을 정해진 순서로 쓰고, 화면에 없는 기존 키(host-managed·trunks 등)는 뒤에 남긴다(type 은 뺀다).</summary>
    internal static string FormatNetwork(IReadOnlyDictionary<string, string> values, PropertyString original)
    {
        var v4Static = V(values, "v4") != "dhcp";
        var v6Mode = V(values, "v6");
        var v6Static = v6Mode is not ("dhcp" or "auto");
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = V(values, "name"), ["hwaddr"] = V(values, "hwaddr"), ["bridge"] = V(values, "bridge"),
            ["tag"] = V(values, "tag"), ["firewall"] = V(values, "firewall") == "1" ? "1" : "",
            ["ip"] = v4Static ? V(values, "ip") : "dhcp", ["gw"] = v4Static ? V(values, "gw") : "",
            ["ip6"] = v6Static ? V(values, "ip6") : v6Mode, ["gw6"] = v6Static ? V(values, "gw6") : "",
            ["link_down"] = V(values, "link_down") == "1" ? "1" : "", ["mtu"] = V(values, "mtu"),
            ["rate"] = V(values, "rate")
        };
        var parts = FormKeys.Where(k => map[k].Length > 0).Select(k => $"{k}={map[k]}").ToList();
        parts.AddRange(original.Items.Where(kv => !FormKeys.Contains(kv.Key) && kv.Key != "type")
            .Select(kv => kv.Value.Length > 0 ? $"{kv.Key}={kv.Value}" : kv.Key));
        return string.Join(',', parts);
    }
}

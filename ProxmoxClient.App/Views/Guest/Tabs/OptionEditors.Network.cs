using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>옵션 편집기 — CT 기능과 Cloud-Init IP 설정.</summary>
internal static partial class OptionEditors
{
    // ------------------------------------------------------------ CT 기능 (features)

    /// <summary>
    ///     CT 기능 — keyctl 은 비특권 CT 만, NFS·CIFS 마운트는 특권 CT 만 쓸 수 있다(웹 UI 와 같이 해당 칸만 보인다).
    ///     저장: 이름순(fuse, keyctl, mknod, mount, nesting). 이미 있던 다른 마운트 종류는 앞에 남긴다.
    /// </summary>
    public static OptionEditor Features(string key)
    {
        return new OptionEditor
        {
            Fields = (raw, config) =>
            {
                var p = PropertyString.Parse(raw);
                var mounts = MountTypes(p);
                var unprivileged = config.GetValueOrDefault("unprivileged") == "1";
                var fields = new List<FormField>();
                if (unprivileged) fields.Add(Check("keyctl", "Features_Keyctl", p.IsOn("keyctl")));
                fields.Add(Check("nesting", "Features_Nesting", p.IsOn("nesting")));
                if (!unprivileged)
                {
                    fields.Add(Check("nfs", "Features_Nfs", mounts.Contains("nfs")));
                    fields.Add(Check("cifs", "Features_Cifs", mounts.Contains("cifs")));
                }

                fields.Add(Check("fuse", "Features_Fuse", p.IsOn("fuse")));
                fields.Add(Check("mknod", "Features_Mknod", p.IsOn("mknod"), Loc.T("Features_Experimental")));
                return fields;
            },
            Build = (values, raw) => One(key, FormatFeatures(values, PropertyString.Parse(raw))),
            Display = raw => raw
        };
    }

    private static FormField Check(string key, string labelKey, bool on, string? hint = null)
    {
        return new FormField
        {
            Key = key, LabelKey = labelKey, Kind = FormFieldKind.Bool, Initial = on ? "1" : "0", Hint = hint
        };
    }

    private static List<string> MountTypes(PropertyString features)
    {
        return features.Get("mount").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>체크 결과로 features 를 만든다 — 편집 창에 없는 마운트 종류는 원래 값에서 가져와 남긴다.</summary>
    internal static string FormatFeatures(IReadOnlyDictionary<string, string> values, PropertyString original)
    {
        bool On(string k) => values.GetValueOrDefault(k) == "1";

        var mounts = MountTypes(original).Where(m => m is not ("nfs" or "cifs")).ToList();
        if (On("nfs")) mounts.Add("nfs");
        if (On("cifs")) mounts.Add("cifs");

        var parts = new List<string>();
        if (On("fuse")) parts.Add("fuse=1");
        if (On("keyctl")) parts.Add("keyctl=1");
        if (On("mknod")) parts.Add("mknod=1");
        if (mounts.Count > 0) parts.Add("mount=" + string.Join(';', mounts));
        if (On("nesting")) parts.Add("nesting=1");
        return string.Join(',', parts);
    }

    // ------------------------------------------------------------ Cloud-Init IP 설정 (ipconfigN)

    /// <summary>
    ///     네트워크 장치 하나의 IP 설정 — IPv4 는 고정/DHCP, IPv6 는 고정/DHCP/SLAAC.
    ///     저장 순서 ip, gw, ip6, gw6 (빈 값 제외). 모두 비면 삭제.
    /// </summary>
    public static OptionEditor IpConfig(string key)
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            {
                var p = PropertyString.Parse(raw);
                var v4 = p.Get("ip") == "dhcp" ? "dhcp" : "static";
                var ip6 = p.Get("ip6");
                var v6 = ip6 is "dhcp" or "auto" ? ip6 : "static";
                return
                [
                    new FormField { Key = "v4", LabelKey = "IpConfig_V4Mode", Kind = FormFieldKind.Choice,
                        Choices = [("static", "IpConfig_Static"), ("dhcp", "DHCP")], Initial = v4 },
                    new FormField { Key = "ip", LabelKey = "IpConfig_V4Cidr",
                        Initial = v4 == "static" ? p.Get("ip") : "", Hint = Loc.T("IpConfig_StaticOnly") },
                    new FormField { Key = "gw", LabelKey = "IpConfig_V4Gateway", Initial = p.Get("gw") },
                    new FormField { Key = "v6", LabelKey = "IpConfig_V6Mode", Kind = FormFieldKind.Choice,
                        Choices = [("static", "IpConfig_Static"), ("dhcp", "DHCP"), ("auto", "SLAAC")], Initial = v6 },
                    new FormField { Key = "ip6", LabelKey = "IpConfig_V6Cidr",
                        Initial = v6 == "static" ? p.Get("ip6") : "", Hint = Loc.T("IpConfig_StaticOnly") },
                    new FormField { Key = "gw6", LabelKey = "IpConfig_V6Gateway", Initial = p.Get("gw6") }
                ];
            },
            Build = (values, _) => One(key, FormatIpConfig(values))
        };
    }

    internal static string FormatIpConfig(IReadOnlyDictionary<string, string> values)
    {
        var v4Static = V(values, "v4") != "dhcp";
        var v6Mode = V(values, "v6");
        var v6Static = v6Mode is not ("dhcp" or "auto");
        var parts = new List<(string, string)>
        {
            ("ip", v4Static ? V(values, "ip") : "dhcp"),
            ("gw", v4Static ? V(values, "gw") : ""),
            ("ip6", v6Static ? V(values, "ip6") : v6Mode),
            ("gw6", v6Static ? V(values, "gw6") : "")
        };
        return string.Join(',', parts.Where(p => p.Item2.Length > 0).Select(p => $"{p.Item1}={p.Item2}"));
    }
}

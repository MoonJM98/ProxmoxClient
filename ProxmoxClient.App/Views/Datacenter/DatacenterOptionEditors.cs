using System.Globalization;
using System.Text.RegularExpressions;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Guest.Tabs;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     데이터센터 옵션의 여러 칸 편집기(웹 UI dc/OptionView.js) — 서버는 GET 에서 객체로 주고 PUT 에서는
///     <c>key=value,...</c> 문자열을 받으므로, 칸으로 나눠 고친 뒤 그 형식으로 다시 모은다. 모두 비우면 삭제(기본값).
/// </summary>
internal static partial class DatacenterOptionEditors
{
    private const int KibPerMib = 1024;

    [GeneratedRegex("^http://.*$")]
    private static partial Regex HttpProxyPattern();

    [GeneratedRegex(@"^[\w+.\-]+@[\w\-]+(\.[\w\-]+)*$")]
    private static partial Regex EmailPattern();

    /// <summary>유니캐스트(첫 바이트 짝수) 접두사 1~3바이트 — 웹 UI MacPrefix 검사와 같다.</summary>
    [GeneratedRegex("^[a-f0-9][02468ace](?::[a-f0-9]{2}){0,2}:?$", RegexOptions.IgnoreCase)]
    private static partial Regex MacPrefixPattern();

    private static string V(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
    }

    private static IReadOnlyDictionary<string, string> One(string key, string value)
    {
        return new Dictionary<string, string> { [key] = value };
    }

    /// <summary>
    ///     원래 값에 편집한 키만 바꿔 넣는다 — 빈 칸은 빼고, 이 편집기가 모르는 하위 키(새 서버 버전의 옵션)는
    ///     그대로 둔다. 모두 비면 빈 문자열(→ 삭제).
    /// </summary>
    private static string Merge(string raw, params (string Key, string Value)[] parts)
    {
        return PropertyString.Parse(raw)
            .With(parts.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Format();
    }

    /// <summary>한 칸짜리 글자 입력 + 형식 검사(비우면 삭제).</summary>
    public static OptionEditor Checked(string key, string labelKey, Func<string, bool> isValid, string errorKey,
        string? hintKey = null)
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            [
                new FormField { Key = key, LabelKey = labelKey, Initial = raw, Trim = true,
                    Hint = hintKey is null ? null : Loc.T(hintKey) }
            ],
            Validate = values => V(values, key) is { Length: > 0 } v && !isValid(v) ? Loc.T(errorKey) : null,
            Build = (values, _) => One(key, V(values, key))
        };
    }

    public static OptionEditor HttpProxy()
    {
        return Checked("http_proxy", "DcOptions_HttpProxy", v => HttpProxyPattern().IsMatch(v), "DcOpt_BadProxy",
            "DcOpt_ProxyHint");
    }

    public static OptionEditor EmailFrom()
    {
        return Checked("email_from", "DcOptions_EmailFrom", v => EmailPattern().IsMatch(v), "DcOpt_BadEmail",
            "DcOpt_EmailHint");
    }

    public static OptionEditor MacPrefix()
    {
        return Checked("mac_prefix", "DcOptions_MacPrefix", v => MacPrefixPattern().IsMatch(v), "DcOpt_BadMacPrefix",
            "DcOpt_MacPrefixHint");
    }

    public static OptionEditor MaxWorkers()
    {
        return Checked("max_workers", "DcOptions_MaxWorkers", v => int.TryParse(v, out var n) && n >= 1,
            "DcOpt_BadMaxWorkers", "DcOpt_MaxWorkersHint");
    }

    /// <summary>이전·복제 네트워크 — type(secure/insecure) + network(CIDR). 예: type=secure,network=10.1.0.0/24</summary>
    public static OptionEditor MigrationLike(string key, string labelKey)
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            {
                var p = PropertyString.Parse(raw, "type");
                return
                [
                    new FormField { Key = "type", LabelKey = "DcOpt_MigrationType", Kind = FormFieldKind.Choice,
                        Initial = p.Get("type"),
                        Choices = [("", "DcOptions_Default"), ("secure", "secure"), ("insecure", "insecure")] },
                    new FormField { Key = "network", LabelKey = "DcOpt_Network", Initial = p.Get("network"),
                        Trim = true, Hint = Loc.T("DcOpt_NetworkHint") }
                ];
            },
            Validate = values => V(values, "network") is { Length: > 0 } n && !n.Contains('/')
                ? Loc.T("Wz_NeedCidr")
                : null,
            Build = (values, raw) => One(key, Merge(raw,
                ("type", V(values, "type")), ("network", V(values, "network"))))
        };
    }

    /// <summary>HA 종료 정책 — shutdown_policy=freeze|failover|migrate|conditional.</summary>
    public static OptionEditor Ha()
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            [
                new FormField { Key = "shutdown_policy", LabelKey = "DcOpt_ShutdownPolicy",
                    Kind = FormFieldKind.Choice,
                    Initial = PropertyString.Parse(raw, "shutdown_policy").Get("shutdown_policy"),
                    Choices =
                    [
                        ("", "DcOpt_PolicyDefault"), ("freeze", "freeze"), ("failover", "failover"),
                        ("migrate", "migrate"), ("conditional", "conditional")
                    ] }
            ],
            Build = (values, raw) => One("ha", Merge(raw, ("shutdown_policy", V(values, "shutdown_policy"))))
        };
    }

    /// <summary>클러스터 자원 배치(CRS) — ha=basic|static, ha-rebalance-on-start.</summary>
    public static OptionEditor Crs()
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            {
                var p = PropertyString.Parse(raw, "ha");
                return
                [
                    new FormField { Key = "ha", LabelKey = "DcOpt_CrsHa", Kind = FormFieldKind.Choice,
                        Initial = p.Get("ha"),
                        Choices = [("", "DcOpt_CrsDefault"), ("basic", "basic"), ("static", "static")] },
                    new FormField { Key = "ha-rebalance-on-start", LabelKey = "DcOpt_CrsRebalance",
                        Kind = FormFieldKind.Bool, Initial = p.IsOn("ha-rebalance-on-start") ? "1" : "0" }
                ];
            },
            Build = (values, raw) => One("crs", Merge(raw, ("ha", V(values, "ha")),
                ("ha-rebalance-on-start", V(values, "ha-rebalance-on-start") == "1" ? "1" : "")))
        };
    }

    private static readonly string[] BwlimitKeys = ["default", "restore", "migration", "clone", "move"];

    private static string BwlimitLabel(string key)
    {
        return key switch
        {
            "restore" => "DcOpt_Bw_restore",
            "migration" => "DcOpt_Bw_migration",
            "clone" => "DcOpt_Bw_clone",
            "move" => "DcOpt_Bw_move",
            _ => "DcOpt_Bw_default"
        };
    }

    /// <summary>대역폭 제한 — 화면은 MiB/s, 서버는 KiB/s 정수(웹 UI 와 같다).</summary>
    public static OptionEditor Bwlimit()
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            {
                var p = PropertyString.Parse(raw);
                return BwlimitKeys.Select(k => new FormField
                {
                    Key = k, LabelKey = BwlimitLabel(k), Trim = true, Hint = "MiB/s",
                    Initial = long.TryParse(p.Get(k), out var kib)
                        ? (kib / (double)KibPerMib).ToString("0.##", CultureInfo.InvariantCulture)
                        : string.Empty
                }).ToList();
            },
            Validate = values => BwlimitKeys.All(k => V(values, k).Length == 0 || ParseMib(V(values, k)) is > 0)
                ? null
                : Loc.T("DcOpt_BadBwlimit"),
            Build = (values, raw) => One("bwlimit", Merge(raw, BwlimitKeys
                .Select(k => (k, ParseMib(V(values, k)) is { } mib
                    ? ((long)Math.Round(mib * KibPerMib)).ToString(CultureInfo.InvariantCulture)
                    : string.Empty)).ToArray())),
            Display = raw => string.Join(", ", PropertyString.Parse(raw).Items
                .Select(kv => long.TryParse(kv.Value, out var kib)
                    ? $"{kv.Key}={(kib / (double)KibPerMib).ToString("0.##", CultureInfo.InvariantCulture)} MiB/s"
                    : $"{kv.Key}={kv.Value}"))
        };
    }

    private static double? ParseMib(string text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>다음 VMID 범위 — lower, upper (100~999999999, lower ≤ upper).</summary>
    public static OptionEditor NextId()
    {
        const long Min = 100, Max = 1_000_000_000;
        return new OptionEditor
        {
            Fields = (raw, _) =>
            {
                var p = PropertyString.Parse(raw);
                return
                [
                    new FormField { Key = "lower", LabelKey = "DcOpt_NextIdLower", Initial = p.Get("lower"),
                        Trim = true, Hint = "100" },
                    new FormField { Key = "upper", LabelKey = "DcOpt_NextIdUpper", Initial = p.Get("upper"),
                        Trim = true, Hint = "1000000" }
                ];
            },
            Validate = values =>
            {
                long? Num(string k) => long.TryParse(V(values, k), out var n) ? n : null;
                var lower = Num("lower");
                var upper = Num("upper");
                if ((V(values, "lower").Length > 0 && lower is not (>= Min and <= Max))
                    || (V(values, "upper").Length > 0 && upper is not (>= Min and <= Max)))
                    return Loc.T("DcOpt_BadNextId", Min, Max);
                return lower > upper ? Loc.T("DcOpt_NextIdOrder") : null;
            },
            Build = (values, raw) => One("next-id", Merge(raw,
                ("lower", V(values, "lower")), ("upper", V(values, "upper"))))
        };
    }
}

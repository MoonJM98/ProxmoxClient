using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>인증 영역 — 2단계 인증 강제(tfa), 결과 조립, 검사, 동기화 창.</summary>
internal static partial class RealmActions
{
    private static readonly (string, string)[] TfaTypes =
        [("", "DcRealms_TfaNone"), ("oath", "OATH / TOTP"), ("yubico", "Yubico")];

    /// <summary>영역 전체에 2단계 인증을 강제 — tfa=type=oath,step=30,digits=6 / type=yubico,id=,key=,url=.</summary>
    private static IEnumerable<FormField> TfaFields(string tfa)
    {
        var p = PropertyString.Parse(tfa, "type");
        yield return new FormField { Key = "_tfa", LabelKey = "DcRealms_TfaSection", Kind = FormFieldKind.Section };
        yield return new FormField { Key = "tfa-type", LabelKey = "DcRealms_TfaType", Kind = FormFieldKind.Choice,
            Choices = TfaTypes, Initial = p.Get("type"), Hint = Loc.T("DcRealms_TfaHint") };
        yield return new FormField { Key = "tfa-step", LabelKey = "DcRealms_TfaStep", Initial = p.Get("step"),
            Trim = true, Advanced = true, Hint = Loc.T("DcRealms_TfaStepHint") };
        yield return new FormField { Key = "tfa-digits", LabelKey = "DcRealms_TfaDigits", Initial = p.Get("digits"),
            Trim = true, Advanced = true, Hint = Loc.T("DcRealms_TfaDigitsHint") };
        yield return new FormField { Key = "tfa-id", LabelKey = "DcRealms_YubicoId", Initial = p.Get("id"),
            Trim = true, Advanced = true };
        yield return new FormField { Key = "tfa-key", LabelKey = "DcRealms_YubicoKey", Initial = p.Get("key"),
            Trim = true, Advanced = true };
        yield return new FormField { Key = "tfa-url", LabelKey = "DcRealms_YubicoUrl", Initial = p.Get("url"),
            Trim = true, Advanced = true };
    }

    /// <summary>화면 칸 → 서버 값: 표시 칸 제외, tfa·sync-defaults-options 를 하나씩으로 모은다.</summary>
    internal static Dictionary<string, string> Collect(string type, IReadOnlyDictionary<string, string> values)
    {
        var form = values.Where(kv => !kv.Key.StartsWith('_') && !kv.Key.StartsWith("tfa-")
                                      && !kv.Key.StartsWith("sync-"))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        form["tfa"] = TfaValue(values);
        if (type is "ldap" or "ad")
        {
            var sync = new List<string>();
            if (V(values, "sync-scope") is { Length: > 0 } scope) sync.Add($"scope={scope}");
            sync.Add($"enable-new={(V(values, "sync-enable-new") == "1" ? 1 : 0)}");
            if (V(values, "sync-remove-vanished") is { Length: > 0 } vanished)
                sync.Add($"remove-vanished={vanished.Replace(',', ';')}");
            form["sync-defaults-options"] = string.Join(',', sync);
            // 연결 확인은 저장할 때만 쓰는 요청 값이다 — 끄면 보내지 않는다
            if (V(values, "check-connection") != "1") form.Remove("check-connection");
        }

        return form;
    }

    private static string TfaValue(IReadOnlyDictionary<string, string> values)
    {
        return V(values, "tfa-type") switch
        {
            "oath" => Join(("type", "oath"), ("step", V(values, "tfa-step")), ("digits", V(values, "tfa-digits"))),
            "yubico" => Join(("type", "yubico"), ("id", V(values, "tfa-id")), ("key", V(values, "tfa-key")),
                ("url", V(values, "tfa-url"))),
            _ => string.Empty
        };
    }

    private static string Join(params (string Key, string Value)[] parts)
    {
        return string.Join(',', parts.Where(p => p.Value.Length > 0).Select(p => $"{p.Key}={p.Value}"));
    }

    private static string V(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
    }

    internal static string? Validate(string type, IReadOnlyDictionary<string, string> values)
    {
        if (V(values, "port") is { Length: > 0 } port && !(int.TryParse(port, out var p) && p is >= 1 and <= 65535))
            return Loc.T("DcStorage_BadPort");
        if (V(values, "tfa-type") == "oath"
            && (V(values, "tfa-digits") is { Length: > 0 } d
                && !(int.TryParse(d, out var digits) && digits is >= 6 and <= 8)
                || V(values, "tfa-step") is { Length: > 0 } s && !(int.TryParse(s, out var step) && step >= 10)))
            return Loc.T("DcRealms_BadOath");
        if (V(values, "tfa-type") == "yubico" && (V(values, "tfa-id").Length == 0 || V(values, "tfa-key").Length == 0))
            return Loc.T("DcRealms_YubicoNeeded");
        return type == "openid" && !V(values, "issuer-url").StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? Loc.T("DcRealms_BadIssuer")
            : null;
    }

    /// <summary>
    ///     LDAP·AD 동기화 — 범위·새 사용자 사용·사라진 항목 제거(ACL·항목·속성)·시험 실행.
    ///     처음 값은 영역의 동기화 기본값. OpenID 는 로그인할 때 만들어지므로 동기화가 없다.
    /// </summary>
    private static async Task<string?> SyncAsync(ProxmoxApiClient api, IReadOnlyDictionary<string, string> row,
        Window? owner)
    {
        if (Value(row, "type") is not ("ldap" or "ad")) return Loc.T("DcRealms_SyncUnsupported");

        var config = await api.Realms.GetAsync(row["realm"]);
        var defaults = PropertyString.Parse(PropertyString.FromJsonObject(
            config.TryGetValue("sync-defaults-options", out var d) ? d : string.Empty));
        return await SubmitTaskAsync(api, owner, Loc.T("DcRealms_SyncTitle", row["realm"]),
        [
            new FormField { Key = "scope", LabelKey = "DcRealms_SyncScope", Kind = FormFieldKind.Choice,
                Initial = defaults.Get("scope", "both"),
                Choices = [("both", "DcRealms_SyncBoth"), ("users", "DcTab_Users"), ("groups", "DcTab_Groups")] },
            new FormField { Key = "enable-new", LabelKey = "DcRealms_EnableNew", Kind = FormFieldKind.Bool,
                Initial = defaults.Get("enable-new", "1") is "0" ? "0" : "1" },
            new FormField { Key = "remove-vanished", LabelKey = "DcRealms_RemoveVanished",
                Kind = FormFieldKind.MultiChoice, Choices = Vanished,
                Initial = defaults.Get("remove-vanished").Replace(';', ','), Hint = Loc.T("DcRealms_VanishedHint") },
            new FormField { Key = "dry-run", LabelKey = "DcRealms_DryRun", Kind = FormFieldKind.Bool, Initial = "1" }
        ], values =>
        {
            var form = NonEmpty(values);
            if (form.TryGetValue("remove-vanished", out var vanished))
                form["remove-vanished"] = vanished.Replace(',', ';');
            return api.Realms.SyncAsync(row["realm"], form);
        }, "DcRealms_Synced");
    }
}

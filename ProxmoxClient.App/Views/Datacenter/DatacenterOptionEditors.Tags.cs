using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Guest.Tabs;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>태그·인증(WebAuthn/U2F)·동의 문구 편집기.</summary>
internal static partial class DatacenterOptionEditors
{
    /// <summary>태그 모양 — shape, ordering, case-sensitive, color-map(tag:배경[:글자];…).</summary>
    public static OptionEditor TagStyle()
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            {
                var p = PropertyString.Parse(raw);
                return
                [
                    new FormField { Key = "shape", LabelKey = "DcOpt_TagShape", Kind = FormFieldKind.Choice,
                        Initial = p.Get("shape"),
                        Choices = [("", "DcOptions_Default"), ("full", "DcOpt_ShapeFull"),
                            ("circle", "DcOpt_ShapeCircle"), ("dense", "DcOpt_ShapeDense"),
                            ("none", "DcOpt_ShapeNone")] },
                    new FormField { Key = "ordering", LabelKey = "DcOpt_TagOrdering", Kind = FormFieldKind.Choice,
                        Initial = p.Get("ordering"),
                        Choices = [("", "DcOptions_Default"), ("config", "DcOpt_OrderConfig"),
                            ("alphabetical", "DcOpt_OrderAlpha")] },
                    new FormField { Key = "case-sensitive", LabelKey = "DcOpt_TagCase", Kind = FormFieldKind.Bool,
                        Initial = p.IsOn("case-sensitive") ? "1" : "0" },
                    new FormField { Key = "color-map", LabelKey = "DcOpt_TagColors", Initial = p.Get("color-map"),
                        Trim = true, Hint = Loc.T("DcOpt_TagColorsHint") }
                ];
            },
            Build = (values, raw) => One("tag-style", Merge(raw,
                ("case-sensitive", V(values, "case-sensitive") == "1" ? "1" : ""),
                ("color-map", V(values, "color-map")), ("ordering", V(values, "ordering")),
                ("shape", V(values, "shape"))))
        };
    }

    /// <summary>사용자 태그 권한 — user-allow(none/list/existing/free), user-allow-list(; 구분).</summary>
    public static OptionEditor UserTagAccess()
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            {
                var p = PropertyString.Parse(raw);
                return
                [
                    new FormField { Key = "user-allow", LabelKey = "DcOpt_UserAllow", Kind = FormFieldKind.Choice,
                        Initial = p.Get("user-allow"),
                        Choices = [("", "DcOpt_UserAllowDefault"), ("none", "DcOpt_AllowNone"),
                            ("list", "DcOpt_AllowList"), ("existing", "DcOpt_AllowExisting"),
                            ("free", "DcOpt_AllowFree")] },
                    new FormField { Key = "user-allow-list", LabelKey = "DcOpt_UserAllowList", Trim = true,
                        Initial = p.Get("user-allow-list"), Hint = Loc.T("DcOpt_TagListHint") }
                ];
            },
            Build = (values, raw) => One("user-tag-access", Merge(raw, ("user-allow", V(values, "user-allow")),
                ("user-allow-list", TagList(V(values, "user-allow-list")))))
        };
    }

    /// <summary>등록 태그 — 관리자만 붙일 수 있는 태그 목록(; 구분). GET 은 배열("a, b")로 온다.</summary>
    public static OptionEditor RegisteredTags()
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            [
                new FormField { Key = "registered-tags", LabelKey = "DcOpt_RegisteredTags", Trim = true,
                    Initial = TagList(raw), Hint = Loc.T("DcOpt_TagListHint") }
            ],
            Build = (values, _) => One("registered-tags", TagList(V(values, "registered-tags")))
        };
    }

    /// <summary>쉼표·세미콜론·공백으로 적은 태그를 서버 형식(;)으로.</summary>
    private static string TagList(string text)
    {
        return string.Join(';', text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>WebAuthn — rp(이름), origin, id(도메인). 모두 비우면 삭제.</summary>
    public static OptionEditor WebAuthn()
    {
        return Composite("webauthn", ("rp", "DcOpt_WebAuthnRp", null),
            ("origin", "DcOpt_Origin", "DcOpt_OriginHint"), ("id", "DcOpt_WebAuthnId", "DcOpt_WebAuthnIdHint"));
    }

    /// <summary>U2F(구형) — appid, origin.</summary>
    public static OptionEditor U2f()
    {
        return Composite("u2f", ("appid", "DcOpt_U2fAppId", null), ("origin", "DcOpt_Origin", "DcOpt_OriginHint"));
    }

    private static OptionEditor Composite(string key, params (string Key, string LabelKey, string? HintKey)[] parts)
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            {
                var p = PropertyString.Parse(raw);
                return parts.Select(f => new FormField
                {
                    Key = f.Key, LabelKey = f.LabelKey, Initial = p.Get(f.Key), Trim = true,
                    Hint = f.HintKey is null ? null : Loc.T(f.HintKey)
                }).ToList();
            },
            Build = (values, raw) => One(key, Merge(raw, parts.Select(f => (f.Key, V(values, f.Key))).ToArray()))
        };
    }

    private static string DecodeBase64(string raw)
    {
        try
        {
            return raw.Length == 0 ? raw : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        }
        catch (FormatException)
        {
            return raw;
        }
    }

    /// <summary>로그인 동의 문구 — 여러 줄(최대 64KiB, 웹 UI 와 같다).</summary>
    public static OptionEditor ConsentText()
    {
        const int MaxLength = 64 * 1024;
        return new OptionEditor
        {
            Fields = (raw, _) =>
            [
                new FormField { Key = "consent-text", LabelKey = "DcOpt_ConsentText", Kind = FormFieldKind.Multiline,
                    Initial = DecodeBase64(raw) }
            ],
            Validate = values => V(values, "consent-text").Length > MaxLength ? Loc.T("DcOpt_ConsentTooLong") : null,
            // 서버(datacenter.cfg)는 줄 단위 설정이라 여러 줄 글을 base64 로 담는다
            Build = (values, _) => One("consent-text", V(values, "consent-text") is { Length: > 0 } text
                ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")))
                : string.Empty),
            Display = raw => DecodeBase64(raw).Split('\n')[0]
        };
    }
}

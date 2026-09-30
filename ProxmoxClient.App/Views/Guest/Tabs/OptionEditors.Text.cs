using System.Text;
using System.Text.RegularExpressions;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>옵션 편집기 — 글자 입력 위주 항목(SMBIOS·DNS·SSH 키·메모·RTC 시작 날짜 등).</summary>
internal static partial class OptionEditors
{
    private static readonly (string Key, string LabelKey)[] SmbiosTextFields =
    [
        ("manufacturer", "Smbios_Manufacturer"), ("product", "Smbios_Product"), ("version", "Smbios_Version"),
        ("serial", "Smbios_Serial"), ("sku", "Smbios_Sku"), ("family", "Smbios_Family")
    ];

    [GeneratedRegex(@"^(now|\d{4}-\d{1,2}-\d{1,2}(T\d{1,2}:\d{1,2}:\d{1,2})?)$")]
    private static partial Regex StartDatePattern();

    [GeneratedRegex(@"^[a-fA-F0-9]{8}(-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}$")]
    private static partial Regex UuidPattern();

    // ------------------------------------------------------------ SMBIOS (smbios1)

    /// <summary>SMBIOS type1 — uuid 외 값은 base64 로 저장한다(웹 UI 와 같이 ",base64=1" 을 붙인다).</summary>
    public static OptionEditor Smbios(string key)
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            {
                var values = ParseSmbios(raw);
                return
                [
                    new FormField
                    {
                        Key = "uuid", LabelKey = "Smbios_Uuid", Initial = values.GetValueOrDefault("uuid", "")
                    },
                    .. SmbiosTextFields.Select(f => new FormField
                    {
                        Key = f.Key, LabelKey = f.LabelKey, Initial = values.GetValueOrDefault(f.Key, "")
                    })
                ];
            },
            Validate = values => V(values, "uuid") is { Length: > 0 } uuid && !UuidPattern().IsMatch(uuid)
                ? Loc.T("Smbios_UuidInvalid")
                : null,
            Build = (values, _) => One(key, FormatSmbios(values))
        };
    }

    /// <summary>base64=1 이면 uuid 외 값을 풀어서 돌려준다.</summary>
    internal static Dictionary<string, string> ParseSmbios(string raw)
    {
        var p = PropertyString.Parse(raw);
        var encoded = p.IsOn("base64");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in p.Items.Where(kv => kv.Key != "base64"))
            result[k] = encoded && k != "uuid" ? DecodeBase64(v) : v;
        return result;
    }

    internal static string FormatSmbios(IReadOnlyDictionary<string, string> values)
    {
        var parts = new List<string>();
        if (V(values, "uuid") is { Length: > 0 } uuid) parts.Add($"uuid={uuid}");
        var texts = SmbiosTextFields.Select(f => f.Key).Where(f => V(values, f).Length > 0)
            .Select(f => $"{f}={Convert.ToBase64String(Encoding.UTF8.GetBytes(V(values, f)))}").ToList();
        parts.AddRange(texts);
        if (texts.Count > 0) parts.Add("base64=1");
        return string.Join(',', parts);
    }

    private static string DecodeBase64(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return value; // 잘못 저장된 값은 그대로 보여 준다
        }
    }

    // ------------------------------------------------------------ DNS (searchdomain + nameserver, 한 창에서 함께)

    /// <summary>DNS 도메인·서버 — 서버 목록은 공백·쉼표·세미콜론으로 나눠 공백 하나로 다시 잇는다.</summary>
    public static OptionEditor Dns()
    {
        return new OptionEditor
        {
            Fields = (_, config) =>
            [
                new FormField { Key = "searchdomain", LabelKey = "GuestSettingsWindow_25",
                    Initial = config.GetValueOrDefault("searchdomain", ""), Hint = Loc.T("GuestOptions_UseHost") },
                new FormField { Key = "nameserver", LabelKey = "GuestSettingsWindow_23",
                    Initial = config.GetValueOrDefault("nameserver", ""), Hint = Loc.T("Dns_ServersHint") }
            ],
            Build = (values, _) => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["searchdomain"] = V(values, "searchdomain"),
                ["nameserver"] = string.Join(' ', V(values, "nameserver")
                    .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
            }
        };
    }

    // ------------------------------------------------------------ Cloud-Init SSH 공개 키 (URL 인코딩 저장)

    /// <summary>SSH 공개 키 — 서버에는 URL 인코딩으로 저장된다. 편집 창에는 풀어서 여러 줄로 보여 준다.</summary>
    public static OptionEditor SshKeys(string key)
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            [
                new FormField { Key = "keys", LabelKey = "CloudInit_SshKeys", Kind = FormFieldKind.Multiline,
                    Initial = DecodeSshKeys(raw), Trim = false }
            ],
            Build = (values, _) => One(key, values.TryGetValue("keys", out var text) && text.Trim().Length > 0
                ? EncodeUriComponent(text.Replace("\r\n", "\n"))
                : string.Empty),
            Display = raw => string.Join(", ", DecodeSshKeys(raw)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(SshKeyLabel))
        };
    }

    /// <summary>JavaScript encodeURIComponent 와 같게 — ! * ' ( ) 는 그대로 둔다(웹 UI 저장 값과 같아야 바뀜 판단이 맞다).</summary>
    internal static string EncodeUriComponent(string text)
    {
        return Uri.EscapeDataString(text).Replace("%21", "!").Replace("%2A", "*").Replace("%27", "'")
            .Replace("%28", "(").Replace("%29", ")");
    }

    /// <summary>CT 호스트 이름 — 비울 수 없다(웹 UI 는 빈 값을 CT&lt;vmid&gt; 로 보낸다; 삭제 요청은 거부된다).</summary>
    public static OptionEditor Hostname()
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            [
                new FormField { Key = "hostname", LabelKey = "MainWindow_40", Initial = raw, Required = true }
            ],
            Build = (values, _) => One("hostname", V(values, "hostname"))
        };
    }

    internal static string DecodeSshKeys(string raw)
    {
        try
        {
            return Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException)
        {
            return raw;
        }
    }

    /// <summary>키 한 줄의 이름 — 설명(주석)이 있으면 그것, 없으면 키 종류.</summary>
    private static string SshKeyLabel(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var typeIndex = Array.FindIndex(parts, p => p.StartsWith("ssh-", StringComparison.Ordinal)
                                                    || p.StartsWith("ecdsa-", StringComparison.Ordinal)
                                                    || p.StartsWith("sk-", StringComparison.Ordinal));
        if (typeIndex < 0) return line;

        var comment = string.Join(' ', parts.Skip(typeIndex + 2));
        var label = comment.Length > 0 ? comment : parts[typeIndex];
        return typeIndex > 0 ? $"{label} {Loc.T("CloudInit_KeyWithOptions")}" : label;
    }

    // ------------------------------------------------------------ 단순 입력 몇 가지

    /// <summary>여러 줄 메모(description) — 줄바꿈을 그대로 저장한다.</summary>
    public static OptionEditor Notes(string key, string labelKey)
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            [
                new FormField { Key = "text", LabelKey = labelKey, Kind = FormFieldKind.Multiline,
                    Initial = raw, CanLoadFile = false }
            ],
            Build = (values, _) => One(key, V(values, "text")),
            Display = raw => raw.Replace("\r", "").Replace('\n', ' ')
        };
    }

    /// <summary>비밀번호 — 서버는 가린 값만 돌려주므로 빈 칸으로 열고, 비워 두면 삭제.</summary>
    public static OptionEditor Password(string key, string labelKey)
    {
        return new OptionEditor
        {
            Fields = (_, _) =>
            [
                new FormField { Key = "password", LabelKey = labelKey, Kind = FormFieldKind.Password,
                    Hint = Loc.T("CloudInit_PasswordHint") }
            ],
            Build = (values, _) => One(key, values.TryGetValue("password", out var p) ? p : string.Empty)
        };
    }

    /// <summary>RTC 시작 날짜 — now 또는 YYYY-MM-DD(THH:MM:SS).</summary>
    public static OptionEditor StartDate(string key)
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            [
                new FormField { Key = "date", LabelKey = "GuestOptions_StartDate",
                    Initial = raw.Length > 0 ? raw : "now", Hint = Loc.T("StartDate_Hint") }
            ],
            Validate = values => V(values, "date") is { Length: > 0 } date && !StartDatePattern().IsMatch(date)
                ? Loc.T("StartDate_Invalid")
                : null,
            Build = (values, _) => One(key, V(values, "date"))
        };
    }
}

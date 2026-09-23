using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     2단계 인증 등록 — TOTP(인증 앱)와 복구 코드. WebAuthn·YubiKey 는 브라우저 인증 API 가 필요해 웹 UI 에서 한다.
/// </summary>
internal static class TfaRegistration
{
    /// <summary>웹 UI 와 같은 발급자 이름 — 인증 앱 목록에 이 이름으로 보인다.</summary>
    private const string Issuer = "Proxmox Web UI";

    /// <summary>TOTP 비밀 값 길이(160비트, RFC 4226 권장).</summary>
    private const int SecretBytes = 20;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static IReadOnlyList<TableAction> Actions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "DcTfa_AddTotp", IconKey = "IconPlus", Run = (_, owner) => AddTotpAsync(api, owner)
            },
            new TableAction
            {
                LabelKey = "DcTfa_AddRecovery", IconKey = "IconKeyboard",
                Run = (_, owner) => AddRecoveryAsync(api, owner)
            }
        ];
    }

    private static async Task<string?> AddTotpAsync(ProxmoxApiClient api, Window? owner)
    {
        var users = await UserChoicesAsync(api);
        var first = new FormDialog(Loc.T("DcTfa_AddTotp"),
        [
            new FormField
            {
                Key = "userid", LabelKey = "Table_UserId", Kind = FormFieldKind.Choice, Choices = users, Required = true
            },
            new FormField { Key = "description", LabelKey = "Table_Description", Initial = "TOTP" }
        ]) { Owner = owner };
        if (first.ShowDialog() != true || first.Result is not { } target) return null;

        var userId = target["userid"];
        var secret = NewSecret();
        var uri = TotpUri(userId, secret);

        // 인증 앱에 넣을 값 — 복사할 수 있게 글 창으로 보여 준다
        TextViewWindow.ShowModal(owner, Loc.T("DcTfa_SecretTitle"), Loc.T("DcTfa_SecretText", secret, uri));

        return await SubmitAsync(owner, Loc.T("DcTfa_VerifyTitle", userId),
        [
            new FormField { Key = "value", LabelKey = "DcTfa_Code", Required = true },
            new FormField { Key = "password", LabelKey = "DcUsers_MyPassword", Kind = FormFieldKind.Password }
        ], async values =>
        {
            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = "totp", ["totp"] = uri, ["value"] = values["value"],
                ["description"] = target["description"]
            };
            if (values["password"].Length > 0) form["password"] = values["password"];
            await api.SendForObjectAsync(HttpMethod.Post, $"access/tfa/{Seg(userId)}", form);
            return string.Empty;
        }, "DcTfa_Added", titleIsKey: false,
            validate: values => values["value"].Length == 6 && values["value"].All(char.IsAsciiDigit)
                ? null
                : Loc.T("DcTfa_CodeInvalid"));
    }

    /// <summary>복구 코드 — 서버가 만든 일회용 코드를 지금 한 번만 보여 준다.</summary>
    private static async Task<string?> AddRecoveryAsync(ProxmoxApiClient api, Window? owner)
    {
        var users = await UserChoicesAsync(api);
        var dialog = new FormDialog(Loc.T("DcTfa_AddRecovery"),
        [
            new FormField
            {
                Key = "userid", LabelKey = "Table_UserId", Kind = FormFieldKind.Choice, Choices = users, Required = true
            },
            new FormField { Key = "password", LabelKey = "DcUsers_MyPassword", Kind = FormFieldKind.Password }
        ]) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } values) return null;

        var form = new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "recovery" };
        if (values["password"].Length > 0) form["password"] = values["password"];
        var created = await api.SendForObjectAsync(HttpMethod.Post, $"access/tfa/{Seg(values["userid"])}", form);

        var codes = Value(created, "recovery").Replace(", ", Environment.NewLine);
        TextViewWindow.ShowModal(owner, Loc.T("DcTfa_RecoveryTitle"), Loc.T("DcTfa_RecoveryText", codes));
        return Loc.T("DcTfa_Added");
    }

    private static async Task<List<(string, string)>> UserChoicesAsync(ProxmoxApiClient api)
    {
        return (await api.GetTableAsync("access/users"))
            .Select(u => (Value(u, "userid"), Value(u, "userid")))
            .OrderBy(u => u.Item1, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>인증 앱이 읽는 otpauth 주소 — 웹 UI 와 같은 형식(SHA1·6자리·30초).</summary>
    internal static string TotpUri(string userId, string secret)
    {
        var label = Uri.EscapeDataString($"{Issuer}:{userId}");
        return $"otpauth://totp/{label}?secret={secret}&period=30&digits=6&algorithm=SHA1"
               + $"&issuer={Uri.EscapeDataString(Issuer)}";
    }

    internal static string NewSecret()
    {
        return Base32(RandomNumberGenerator.GetBytes(SecretBytes));
    }

    /// <summary>RFC 4648 base32(패딩 없음) — 인증 앱이 받는 비밀 값 형식.</summary>
    internal static string Base32(byte[] data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0) output.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }
}

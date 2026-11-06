using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.App.Views;

/// <summary>2단계 인증 — 비밀번호 로그인 뒤 코드(TOTP·복구 키·Yubico)를 받아 마저 로그인한다.</summary>
public partial class LoginWindow
{
    /// <summary>
    ///     로그인한다. 2단계 인증 계정이면 코드를 묻고, 틀리면 다시 묻는다. 취소하면 <see cref="OperationCanceledException" />.
    ///     메인 화면은 같은 프로필로 다시 로그인하므로, 2단계를 마치면 받은 티켓을 비밀번호 자리에 둔다(PVE 는 티켓으로 재발급).
    /// </summary>
    private async Task LoginWithTfaAsync(ProxmoxApiClient client, ConnectionProfile runtime)
    {
        try
        {
            await client.LoginAsync();
            return;
        }
        catch (TfaRequiredException tfa)
        {
            await CompleteTfaAsync(client, tfa.Challenge);
        }

        runtime.UserName = client.AuthenticatedUser ?? runtime.UserName;
        runtime.Password = SecureStringHelper.FromString(client.AuthTicket ?? string.Empty);
    }

    private async Task CompleteTfaAsync(ProxmoxApiClient client, TfaChallenge challenge)
    {
        var kinds = challenge.SupportedKinds;
        if (kinds.Count == 0) throw new InvalidOperationException(Loc.T("LoginWindow_TfaUnsupported"));

        string? error = challenge.HasWebAuthn || challenge.HasU2f ? Loc.T("LoginWindow_TfaUnsupported") : null;
        var lastKind = TfaChallenge.WireName(kinds[0]);
        while (true)
        {
            var dialog = new FormDialog(Loc.T("LoginWindow_TfaTitle"),
            [
                new()
                {
                    Key = "kind", LabelKey = "LoginWindow_TfaMethod", Kind = FormFieldKind.Choice, Required = true,
                    Initial = lastKind,
                    Choices = kinds.Select(k => (TfaChallenge.WireName(k), KindLabelKey(k))).ToList()
                },
                new() { Key = "code", LabelKey = "LoginWindow_TfaCode", Required = true, Hint = error }
            ]) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is not { } result) throw new OperationCanceledException();

            lastKind = result["kind"];
            var kind = kinds.First(k => TfaChallenge.WireName(k) == lastKind);
            try
            {
                await client.CompleteTfaAsync(challenge, kind, result["code"]);
                return;
            }
            catch (ProxmoxApiException ex)
            {
                error = ex.Message; // 틀린 코드 — 다시 묻는다
            }
        }
    }

    private static string KindLabelKey(TfaKind kind)
    {
        return kind switch
        {
            TfaKind.Totp => "LoginWindow_TfaTotp",
            TfaKind.Recovery => "LoginWindow_TfaRecovery",
            _ => "LoginWindow_TfaYubico"
        };
    }
}

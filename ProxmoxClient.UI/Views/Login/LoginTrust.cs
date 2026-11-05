using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Login;

/// <summary>
///     서버 인증서 신뢰 확인(TOFU) — WPF <c>CertificateTrust</c> 와 같은 흐름. 연결 작업이
///     <see cref="CertificateTrustException" /> 으로 거부되면 지문을 보여 주고, 신뢰하면 프로필에 기록한 뒤 다시 시도한다.
/// </summary>
internal static class LoginTrust
{
    /// <summary>한 작업에서 묻는 최대 횟수(재시도 사이에 인증서가 또 바뀌는 비정상 상황 대비).</summary>
    private const int MaxPrompts = 2;

    /// <summary>
    ///     <paramref name="action" /> 을 실행하고, 인증서 확인이 필요하면 <paramref name="confirm" /> 으로 묻는다.
    ///     거절하면 원래 예외를 그대로 던진다(호출자가 메시지 표시).
    /// </summary>
    public static async Task RunAsync(Func<Task> action, Func<CertificateRejection, Task<bool>> confirm,
        Func<Task>? persist = null)
    {
        for (var prompt = 0;; prompt++)
            try
            {
                await action();
                return;
            }
            catch (CertificateTrustException ex) when (prompt < MaxPrompts)
            {
                if (!await confirm(ex.Rejection)) throw;

                if (persist is not null) await persist();
            }
    }

    /// <summary>지문 확인 대화상자. 신뢰하면 <paramref name="profile" /> 에 지문을 기록하고 true.</summary>
    public static async Task<bool> ConfirmAsync(CertificateRejection rejection, ConnectionProfile profile)
    {
        var details = L.T("Cert_Details",
            rejection.Host,
            ServerCertificateValidator.FormatThumbprint(rejection.Thumbprint),
            rejection.Subject,
            rejection.Issuer,
            rejection.Expires);

        var ok = rejection.IsMismatch
            ? await AppServices.Dialogs.ConfirmAsync(L.T("Cert_MismatchTitle"),
                L.T("Cert_MismatchText", ServerCertificateValidator.FormatThumbprint(rejection.PinnedThumbprint!),
                    details),
                L.T("Cert_Trust"), true)
            : await AppServices.Dialogs.ConfirmAsync(L.T("Cert_NewTitle"), L.T("Cert_NewText", details),
                L.T("Cert_Trust"));
        if (!ok) return false;

        profile.CertificateThumbprint = rejection.Thumbprint;
        return true;
    }
}

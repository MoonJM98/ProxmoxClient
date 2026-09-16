using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.App.Services;

/// <summary>
///     서버 인증서 신뢰 확인(TOFU) UI 흐름 — 연결 작업이 <see cref="CertificateTrustException" /> 으로 거부되면
///     지문을 보여 주고 사용자가 신뢰하면 프로필에 기록·저장한 뒤 작업을 다시 시도한다.
/// </summary>
internal static class CertificateTrust
{
    /// <summary>한 작업에서 묻는 최대 횟수(재시도 사이에 인증서가 또 바뀌는 비정상 상황 대비).</summary>
    private const int MaxPrompts = 2;

    /// <summary>
    ///     <paramref name="action" /> 을 실행하고, 인증서 확인이 필요하면 <paramref name="confirm" /> 으로 묻는다.
    ///     거절하면 원래 예외를 그대로 던진다(호출자가 메시지 표시).
    /// </summary>
    public static async Task RunAsync(Func<Task> action, Func<CertificateRejection, bool> confirm,
        Func<Task>? persist = null)
    {
        for (var prompt = 0;; prompt++)
            try
            {
                await action().ConfigureAwait(true);
                return;
            }
            catch (CertificateTrustException ex) when (prompt < MaxPrompts)
            {
                if (!confirm(ex.Rejection)) throw;

                if (persist is not null) await persist().ConfigureAwait(true);
            }
    }

    /// <summary>지문 확인 대화상자. 신뢰하면 <paramref name="profile" /> 에 지문을 기록하고 true.</summary>
    public static bool Confirm(Window? owner, CertificateRejection rejection, ConnectionProfile profile)
    {
        var details = Loc.T(
            "Cert_Details",
            rejection.Host,
            ServerCertificateValidator.FormatThumbprint(rejection.Thumbprint),
            rejection.Subject,
            rejection.Issuer,
            rejection.Expires);

        string title;
        string text;
        MessageBoxImage image;
        if (rejection.IsMismatch)
        {
            title = Loc.T("Cert_MismatchTitle");
            image = MessageBoxImage.Warning;
            text = Loc.T(
                "Cert_MismatchText",
                ServerCertificateValidator.FormatThumbprint(rejection.PinnedThumbprint!),
                details);
        }
        else
        {
            title = Loc.T("Cert_NewTitle");
            image = MessageBoxImage.Question;
            text = Loc.T("Cert_NewText", details);
        }

        var result = owner is null
            ? ThemedMessageBox.Show(text, title, MessageBoxButton.YesNo, image)
            : ThemedMessageBox.Show(owner, text, title, MessageBoxButton.YesNo, image);
        if (result != MessageBoxResult.Yes) return false;

        profile.CertificateThumbprint = rejection.Thumbprint;
        return true;
    }
}
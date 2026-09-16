using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ProxmoxClient.Core.Localization;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.Core.Api;

/// <summary>신뢰 확인이 필요한 서버 인증서 정보.</summary>
/// <param name="PinnedThumbprint">이전에 신뢰한 지문. null 이면 처음 보는 서버, 아니면 인증서가 바뀐 경우.</param>
public sealed record CertificateRejection(
    string Host,
    string Thumbprint,
    string? PinnedThumbprint,
    string Subject,
    string Issuer,
    string Expires)
{
    public bool IsMismatch => PinnedThumbprint is not null;
}

/// <summary>서버 인증서를 아직 신뢰하지 않았거나(처음 연결) 신뢰한 지문과 달라 연결을 거부했다.</summary>
public sealed class CertificateTrustException(CertificateRejection rejection) : Exception(
    rejection.IsMismatch
        ? Res.T("ServerCertificateValidator_01", rejection.Host)
        : Res.T("ServerCertificateValidator_02", rejection.Host))
{
    public CertificateRejection Rejection { get; } = rejection;
}

/// <summary>
///     서버 인증서 검증(TOFU, 최초 신뢰 후 지문 고정).
///     - 공인 CA 로 검증되고 호스트명이 맞으면 통과.
///     - 아니면(Proxmox 기본 자체 서명 등) 프로필에 저장한 SHA-256 지문과 같을 때만 통과.
///     - 지문이 없거나 다르면 거부하고 거부 정보를 남긴다 — 호출자가 <see cref="CertificateTrustException" /> 으로 바꿔
///     사용자에게 지문 확인을 요청한다(콜백은 TLS 핸드셰이크 스레드라 여기서 UI 를 띄우지 않는다).
/// </summary>
public sealed class ServerCertificateValidator(ConnectionProfile profile)
{
    private CertificateRejection? _pendingRejection;

    /// <summary>가장 최근 거부 정보(이후 검증에 성공하면 비워진다).</summary>
    public CertificateRejection? PendingRejection => Volatile.Read(ref _pendingRejection);

    public bool Validate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            Volatile.Write(ref _pendingRejection, null);
            return true;
        }

        if (certificate is null || !profile.AcceptSelfSignedCertificate) return false; // 옵션을 끈 프로필은 표준 검증만 사용

        var thumbprint = ComputeThumbprint(certificate);
        var pinned = profile.CertificateThumbprint;
        if (pinned is not null && string.Equals(pinned, thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            Volatile.Write(ref _pendingRejection, null);
            return true;
        }

        Volatile.Write(ref _pendingRejection, new CertificateRejection(
            profile.Host, thumbprint, pinned, certificate.Subject, certificate.Issuer,
            certificate.GetExpirationDateString()));
        return false;
    }

    /// <summary>전송 예외가 TLS 인증 실패로 생겼고 거부 정보가 있으면 신뢰 확인 예외를 돌려준다.</summary>
    public CertificateTrustException? TryCreateTrustException(Exception transportError)
    {
        return IsTlsAuthenticationFailure(transportError) && PendingRejection is { } rejection
            ? new CertificateTrustException(rejection)
            : null;
    }

    /// <summary>인증서 DER 의 SHA-256 — 16진수 대문자(구분자 없음).</summary>
    public static string ComputeThumbprint(X509Certificate certificate)
    {
        return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    }

    /// <summary>표시용 "AB:CD:…" 형식(Proxmox 웹 UI 인증서 화면과 같은 모양).</summary>
    public static string FormatThumbprint(string hex)
    {
        return string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
    }

    private static bool IsTlsAuthenticationFailure(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
            if (error is AuthenticationException)
                return true;

        return false;
    }
}
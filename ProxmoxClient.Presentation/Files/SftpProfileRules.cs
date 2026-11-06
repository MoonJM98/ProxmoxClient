using ProxmoxClient.Core.Files;

namespace ProxmoxClient.Presentation.Files;

/// <summary>SFTP 프로필 편집기(두 앱)가 함께 쓰는 규칙 — 키 파일 경로 풀기, 인증 방식이 같은지.</summary>
internal static class SftpProfileRules
{
    /// <summary>입력한 키 파일 경로 → 절대 경로(환경 변수와 ~/ 를 푼다).</summary>
    public static string LocalKeyPath(string input)
    {
        var path = Environment.ExpandEnvironmentVariables(input.Trim());
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return Path.GetFullPath(path);
    }

    /// <summary>사용자·인증 방식·키·인증서 경로·자격 증명 판이 모두 같은가 — 같으면 이번 세션에 받은 비밀값을 계속 쓴다.</summary>
    public static bool SameAuthentication(SftpAccountProfile old, SftpAccountProfile current) =>
        old.UserName == current.UserName && old.AuthenticationMode == current.AuthenticationMode &&
        old.PrivateKeyPath == current.PrivateKeyPath && old.CertificatePath == current.CertificatePath &&
        old.CredentialRevision == current.CredentialRevision;
}

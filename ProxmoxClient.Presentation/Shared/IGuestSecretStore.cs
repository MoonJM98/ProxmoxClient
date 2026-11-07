namespace ProxmoxClient.Presentation.Shared;

/// <summary>
///     게스트 파일 연결(SFTP·SMB·FTP)의 비밀번호·암호 보관 — WPF 는 Windows 자격 증명 관리자, Avalonia 는 앱 비밀값 저장소.
///     항목 이름(target)은 두 앱이 같은 문자열을 쓴다.
/// </summary>
public interface IGuestSecretStore
{
    string? Read(string target);

    /// <param name="userName">함께 적어 둘 사용자 이름 — 보관하지 않는 저장소도 있다(Avalonia 는 설정 파일에 따로 있다).</param>
    void Write(string target, string userName, string secret);

    void Delete(string target);
}

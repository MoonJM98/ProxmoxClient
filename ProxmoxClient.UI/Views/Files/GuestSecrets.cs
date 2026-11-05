using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Files;

/// <summary>
///     파일 연결의 비밀번호·암호 보관 — WPF 의 Windows 자격 증명 관리자 대신 공용 비밀값 저장소(<see cref="AppServices.SecretStore" />)를 쓴다.
///     항목 이름(target)은 WPF 와 같은 문자열이라 기존 설정과 맞물린다. 사용자 이름은 설정 파일에 따로 있어 보관하지 않는다.
/// </summary>
internal static class GuestSecrets
{
    public static string? Read(string target) => AppServices.SecretStore.Get(target);

    public static void Write(string target, string secret) => AppServices.SecretStore.Set(target, secret);

    public static void Delete(string target) => AppServices.SecretStore.Remove(target);
}

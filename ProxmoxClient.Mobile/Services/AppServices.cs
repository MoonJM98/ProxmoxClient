using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.Mobile.Services;

/// <summary>
///     앱 전역 서비스. 플랫폼 헤드(Android)가 시작할 때 <see cref="SecretStore" /> 를 바꿔 넣는다 —
///     넣지 않으면 메모리에만 보관한다.
/// </summary>
public static class AppServices
{
    private static ProfileRepository? _profiles;

    public static ISecretStore SecretStore { get; set; } = new MemorySecretStore();

    /// <summary>프로필 폴더. null 이면 Core 기본값(앱 데이터 폴더 아래 ProxmoxClient/profiles).</summary>
    public static string? ProfilesDirectory { get; set; }

    public static ProfileRepository Profiles =>
        _profiles ??= new ProfileRepository(new ProfileStore(ProfilesDirectory), SecretStore);

    public static Navigator Navigator { get; } = new();

    public static DialogService Dialogs { get; } = new();
}

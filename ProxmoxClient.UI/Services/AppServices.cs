using ProxmoxClient.UI.Platform;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.UI.Services;

/// <summary>
///     앱 전역 서비스. 비밀값 저장소는 OS 별 기본값(<see cref="SecretStores" />)이고,
///     Android 헤드는 시작할 때 키스토어 구현으로 바꿔 넣는다.
/// </summary>
public static class AppServices
{
    private static ProfileRepository? _profiles;

    public static ISecretStore SecretStore { get; set; } = SecretStores.CreateDefault();

    /// <summary>프로필 폴더. null 이면 Core 기본값(앱 데이터 폴더 아래 ProxmoxClient/profiles).</summary>
    public static string? ProfilesDirectory { get; set; }

    public static ProfileRepository Profiles =>
        _profiles ??= new ProfileRepository(new ProfileStore(ProfilesDirectory), SecretStore);

    public static Navigator Navigator { get; } = new();

    public static DialogService Dialogs { get; } = new();
}

using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.Mobile.Services;

/// <summary>
///     연결 프로필 저장소 — 프로필 본문은 Core 의 <see cref="ProfileStore" />(앱 전용 폴더의 JSON),
///     비밀번호·토큰 비밀값은 <see cref="ISecretStore" /> 에 따로 둔다(프로필 JSON 에는 비밀값을 쓰지 않는다).
/// </summary>
public sealed class ProfileRepository(ProfileStore store, ISecretStore secrets)
{
    public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken ct = default)
    {
        return store.LoadAsync(ct);
    }

    public Task SaveAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        return store.SaveAsync(profile, ct);
    }

    public async Task DeleteAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        await store.DeleteAsync(profile.Id, ct).ConfigureAwait(false);
        ForgetSecret(profile);
    }

    /// <summary>프로필 인증 방식에 맞는 저장된 비밀값(비밀번호 또는 토큰). 없으면 null.</summary>
    public string? GetSecret(ConnectionProfile profile)
    {
        return secrets.Get(SecretKey(profile));
    }

    public void RememberSecret(ConnectionProfile profile, string secret)
    {
        // 인증 방식을 바꾸면 이전 방식의 비밀값은 더 쓰지 않으니 지운다
        ForgetSecret(profile);
        secrets.Set(SecretKey(profile), secret);
    }

    public void ForgetSecret(ConnectionProfile profile)
    {
        secrets.Remove(SecretKey(profile, AuthMode.Password));
        secrets.Remove(SecretKey(profile, AuthMode.ApiToken));
    }

    private static string SecretKey(ConnectionProfile profile, AuthMode? mode = null)
    {
        return (mode ?? profile.AuthMode) == AuthMode.ApiToken
            ? $"profile.{profile.Id:N}.token"
            : $"profile.{profile.Id:N}.password";
    }
}

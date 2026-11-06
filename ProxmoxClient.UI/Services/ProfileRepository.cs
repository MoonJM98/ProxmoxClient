using ProxmoxClient.UI.Platform;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.UI.Services;

/// <summary>
///     연결 프로필 저장소 — 프로필 본문은 Core 의 <see cref="ProfileStore" />(앱 전용 폴더의 JSON),
///     비밀번호·토큰 비밀값은 <see cref="ISecretStore" /> 에 따로 둔다(프로필 JSON 에는 비밀값을 쓰지 않는다).
///     프록시 비밀번호는 Windows 면 Core 가 프로필에 암호화해 넣고, 그 밖의 OS 는 여기서 보안 저장소에 둔다
///     (<see cref="ConnectionProfile.ProxyPasswordInProfile" />).
/// </summary>
public sealed class ProfileRepository(ProfileStore store, ISecretStore secrets)
{
    /// <summary>비밀값을 앱을 다시 켜도 남게 저장할 수 있는지(OS 보안 저장소 사용 가능).</summary>
    public bool CanRememberSecrets => secrets.IsPersistent;

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken ct = default)
    {
        var profiles = await store.LoadAsync(ct).ConfigureAwait(false);
        if (!ConnectionProfile.ProxyPasswordInProfile)
            foreach (var profile in profiles)
                LoadProxyPassword(profile);

        return profiles;
    }

    public async Task SaveAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        await store.SaveAsync(profile, ct).ConfigureAwait(false);
        if (!ConnectionProfile.ProxyPasswordInProfile) StoreProxyPassword(profile);
    }

    public async Task DeleteAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        await store.DeleteAsync(profile.Id, ct).ConfigureAwait(false);
        ForgetSecret(profile);
        TryRemove(ProxyKey(profile));
    }

    /// <summary>
    ///     보안 저장소의 프록시 비밀번호를 채운다. 예전 버전이 프로필에 평문으로 남긴 값을 읽어 왔으면(프로필 파일에서는
    ///     이미 지워졌다) 보안 저장소로 옮긴다. 저장소를 읽지 못하면 비밀번호 없이 연다(목록 표시는 막지 않는다).
    /// </summary>
    private void LoadProxyPassword(ConnectionProfile profile)
    {
        try
        {
            if (profile.ProxyPassword is { Length: > 0 } legacy)
                secrets.Set(ProxyKey(profile), legacy);
            else
                profile.ProxyPassword = secrets.Get(ProxyKey(profile));
        }
        catch (Exception)
        {
            // 보안 저장소 오류 — 프록시 비밀번호만 빠진다
        }
    }

    private void StoreProxyPassword(ConnectionProfile profile)
    {
        if (profile.ProxyPassword is { Length: > 0 } password)
            secrets.Set(ProxyKey(profile), password);
        else
            secrets.Remove(ProxyKey(profile));
    }

    private void TryRemove(string key)
    {
        try
        {
            secrets.Remove(key);
        }
        catch (Exception)
        {
            // 지우지 못한 값은 프로필이 없으니 다시 쓰이지 않는다
        }
    }

    private static string ProxyKey(ConnectionProfile profile)
    {
        return $"profile.{profile.Id:N}.proxy";
    }

    /// <summary>프로필 인증 방식에 맞는 저장된 비밀값(비밀번호 또는 토큰). 없으면 null.</summary>
    public string? GetSecret(ConnectionProfile profile)
    {
        try
        {
            return secrets.Get(SecretKey(profile));
        }
        catch (Exception)
        {
            return null; // 저장소를 읽지 못하면 저장값이 없는 것으로 보고 다시 묻는다
        }
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

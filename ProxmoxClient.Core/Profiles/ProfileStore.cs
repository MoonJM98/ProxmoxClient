using System.Text.Json;
using ProxmoxClient.Core.Settings;

namespace ProxmoxClient.Core.Profiles;

/// <summary>
///     Stores each <see cref="ConnectionProfile" /> as its own JSON file under
///     %APPDATA%\ProxmoxClient\profiles\ (overridable directory). Per-profile files
///     keep a single damaged file from affecting the others; writes are atomic
///     (<see cref="AtomicFile" />: temp file + flush + replace with .bak). Corrupt files are preserved as a copy and
///     skipped on load.
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>Creates a store; uses <see cref="DefaultDirectory" /> when none is given.</summary>
    public ProfileStore(string? directory = null)
    {
        ProfilesDirectory = directory ?? DefaultDirectory;
    }

    /// <summary>Default directory: %APPDATA%\ProxmoxClient\profiles.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProxmoxClient",
        "profiles");

    /// <summary>Directory this store reads from / writes to.</summary>
    public string ProfilesDirectory { get; }

    /// <summary>
    ///     Loads all profiles sorted by name. Missing directory yields an empty list;
    ///     unreadable or corrupt files are skipped (never throws).
    /// </summary>
    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken ct = default)
    {
        var list = new List<ConnectionProfile>();
        List<string> files;
        try
        {
            if (!Directory.Exists(ProfilesDirectory)) return list;

            files = Directory.EnumerateFiles(ProfilesDirectory, "*.json").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return list;
        }

        foreach (var file in files)
            if (await TryLoadProfileAsync(file, ct).ConfigureAwait(false) is { } profile)
                list.Add(profile);

        return list
            .OrderBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>Saves (upserts) one profile atomically, keyed by its <see cref="ConnectionProfile.Id" />.</summary>
    public Task SaveAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return SaveCoreAsync(profile, true, ct);
    }

    /// <summary>Deletes a profile file (and its .bak created by this store); missing ids are ignored.</summary>
    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var path = PathFor(id);
        foreach (var target in new[] { path, path + ".bak" })
            if (File.Exists(target))
                File.Delete(target);

        return Task.CompletedTask;
    }

    private string PathFor(Guid id)
    {
        return Path.Combine(ProfilesDirectory, $"{id}.json");
    }

    private Task SaveCoreAsync(ConnectionProfile profile, bool keepBackup, CancellationToken ct)
    {
        return AtomicFile.WriteAllTextAsync(PathFor(profile.Id), JsonSerializer.Serialize(profile, SerializerOptions),
            keepBackup, ct);
    }

    private async Task<ConnectionProfile?> TryLoadProfileAsync(string file, CancellationToken ct)
    {
        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        ConnectionProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<ConnectionProfile>(raw, SerializerOptions);
        }
        catch (JsonException)
        {
            AtomicFile.PreserveCorrupt(file); // 손상 프로필은 사본을 남기고 건너뜀(원본 유지)
            return null;
        }

        if (profile is null) return null;

        // 레거시 마이그레이션: 과거 버전이 디스크에 평문으로 저장한 비밀값 제거(비밀번호·토큰은 삭제, 프록시 비밀번호는 암호화).
        // 이전 내용(평문 포함)이 .bak 에 남으면 안 되므로 백업 없이 교체한다.
        // 저장이 실패해도 프로필은 목록에 표시한다 — 다음 로드에서 다시 시도.
        try
        {
            if (ContainsPlainSecret(raw)) await SaveCoreAsync(profile, false, ct).ConfigureAwait(false);

            await ProtectBackupAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return profile;
    }

    private static bool ContainsPlainSecret(string raw)
    {
        return raw.Contains("\"Password\"") || raw.Contains("\"ApiTokenSecret\"") || raw.Contains("\"ProxyPassword\"");
    }

    /// <summary>
    ///     이전 버전이 남긴 .bak 에 평문 비밀값이 있으면 같은 내용을 암호화 형식으로 다시 쓴다
    ///     (백업의 다른 내용은 보존하고 평문만 없앤다. 해석할 수 없는 백업은 건드리지 않는다).
    /// </summary>
    private static async Task ProtectBackupAsync(string file, CancellationToken ct)
    {
        var backup = file + ".bak";
        if (!File.Exists(backup)) return;

        var raw = await File.ReadAllTextAsync(backup, ct).ConfigureAwait(false);
        if (!ContainsPlainSecret(raw)) return;

        ConnectionProfile? old;
        try
        {
            old = JsonSerializer.Deserialize<ConnectionProfile>(raw, SerializerOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (old is not null)
            await AtomicFile.WriteAllTextAsync(backup, JsonSerializer.Serialize(old, SerializerOptions), false, ct)
                .ConfigureAwait(false);
    }
}
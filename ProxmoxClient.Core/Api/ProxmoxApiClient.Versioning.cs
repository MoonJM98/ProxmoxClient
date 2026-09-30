using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using ProxmoxClient.Core.Api.Versioning;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Api;

/// <summary>서버 버전 — 한 번 읽어 두고, 버전이 필요한 API 를 부르기 전에 확인한다.</summary>
public sealed partial class ProxmoxApiClient
{
    private static readonly ConcurrentDictionary<(Type, string), PveApiVersion> SinceCache = new();
    private PveApiVersion? _serverVersion;
    private volatile bool _versionTried;

    /// <summary>
    ///     접속한 서버의 주.부 버전. 아직 모르면 null(<see cref="GetVersionAsync" /> 또는
    ///     <see cref="GetServerVersionAsync" /> 가 채운다).
    /// </summary>
    public PveApiVersion? ServerVersion => _serverVersion;

    /// <summary>서버 버전을 한 번만 읽는다. /version 을 읽지 못하면(권한 등) null — 기능을 막지 않는다.</summary>
    public async Task<PveApiVersion?> GetServerVersionAsync(CancellationToken ct = default)
    {
        if (_serverVersion is { } known) return known;
        if (_versionTried) return null; // 한 번 못 읽었으면(권한 등) 다시 묻지 않는다
        _versionTried = true;
        try
        {
            await GetVersionAsync(ct).ConfigureAwait(false);
        }
        catch (ProxmoxApiException)
        {
            // 버전을 모르면 새 기능을 막지 않는다 — 서버가 직접 거절한다
        }

        return _serverVersion;
    }

    /// <summary>서버가 since 이상인지. 버전을 모르면 true(막지 않는다).</summary>
    public bool Supports(PveApiVersion since)
    {
        return _serverVersion is not { } server || server >= since;
    }

    /// <summary>서버가 since 이상인지 — 버전을 아직 모르면 먼저 읽는다.</summary>
    public async Task<bool> SupportsAsync(PveApiVersion since, CancellationToken ct = default)
    {
        await GetServerVersionAsync(ct).ConfigureAwait(false);
        return Supports(since);
    }

    /// <summary>
    ///     부른 메서드(caller)에 붙은 <see cref="PveApiAttribute" /> 중 가장 낮은 도입 버전을 요구한다.
    ///     서버가 더 낮으면 요청을 보내지 않고 알아볼 수 있는 오류를 낸다.
    /// </summary>
    internal async Task RequireAsync(Type owner, CancellationToken ct, [CallerMemberName] string member = "")
    {
        var since = SinceOf(owner, member);
        if (since <= PveApiVersion.Minimum) return;
        if (!await SupportsAsync(since, ct).ConfigureAwait(false))
            throw new ProxmoxApiException(Res.T("Api_VersionRequired", since, _serverVersion));
    }

    /// <summary>형식의 메서드(같은 이름의 오버로드 포함)에 붙은 도입 버전 중 가장 낮은 값.</summary>
    public static PveApiVersion SinceOf(Type owner, string member)
    {
        return SinceCache.GetOrAdd((owner, member), key =>
        {
            var versions = key.Item1.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                                 | BindingFlags.NonPublic)
                .Where(m => m.Name == key.Item2)
                .SelectMany(m => m.GetCustomAttributes<PveApiAttribute>())
                .Select(a => a.SinceVersion)
                .ToList();
            return versions.Count == 0 ? PveApiVersion.Minimum : versions.Min();
        });
    }

    private void RememberVersion(string version)
    {
        if (PveApiVersion.TryParse(version, out var parsed)) _serverVersion = parsed;
    }
}

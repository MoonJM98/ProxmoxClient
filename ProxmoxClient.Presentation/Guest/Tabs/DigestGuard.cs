using ProxmoxClient.Core.Api;
using ProxmoxClient.Presentation.Shared;

namespace ProxmoxClient.Presentation.Guest.Tabs;

/// <summary>
///     digest 로 지키는 쓰기 — 읽은 뒤 다른 곳에서 설정이 바뀌어 서버가 거절하면(<see cref="ProxmoxApiException.IsDigestMismatch" />)
///     실패 대신 "다시 불러왔으니 확인 후 다시 하라" 는 문구를 돌려준다. 표(TableTab)는 문구가 오면 목록을 다시 읽는다.
/// </summary>
internal static class DigestGuard
{
    /// <summary>
    ///     행(또는 설정)에 든 digest. 없으면 null — 그때는 보내지 않는다.
    ///     digest 를 보내지 않는 앱(<see cref="IAppParts.SendsDigest" />)도 null.
    /// </summary>
    public static string? Of(IReadOnlyDictionary<string, string>? row)
    {
        return AppParts.Current.SendsDigest && row is not null && row.TryGetValue("digest", out var digest)
                                             && digest.Length > 0
            ? digest
            : null;
    }

    public static async Task<string?> RunAsync(Func<Task<string?>> work)
    {
        try
        {
            return await work();
        }
        catch (ProxmoxApiException ex) when (ex.IsDigestMismatch)
        {
            return UiText.T("Digest_Changed");
        }
    }

    /// <summary>표 단추의 실행을 감싼다(확인 문구 등 나머지는 그대로).</summary>
    public static TableAction Wrap(TableAction action)
    {
        var run = action.Run;
        return action with { Run = (row, owner) => RunAsync(() => run(row, owner)) };
    }
}

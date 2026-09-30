using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     ACME(데이터센터) — 계정·DNS 플러그인·디렉터리·약관. 계정의 외부 계정 연결(eab-kid·eab-hmac-key)은 8.1+.
/// </summary>
public sealed class AcmeApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    [PveApi("GET", "/cluster/acme/account")]
    public Task<IReadOnlyList<Row>> ListAccountsAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/acme/account", ct);
    }

    /// <summary>계정 정보(보기 좋게 들여쓴 JSON).</summary>
    [PveApi("GET", "/cluster/acme/account/{name}")]
    public Task<string> AccountJsonAsync(string name, CancellationToken ct = default)
    {
        return Api.GetPrettyJsonAsync($"cluster/acme/account/{Seg(name)}", ct);
    }

    /// <summary>계정 등록(작업 UPID) — name·contact·directory·tos_url.</summary>
    [PveApi("POST", "/cluster/acme/account")]
    [PveParam("eab-kid", "8.1")]
    [PveParam("eab-hmac-key", "8.1")]
    public Task<string> RegisterAccountAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PostActionAsync("cluster/acme/account", Supported(form), ct);
    }

    /// <summary>계정 연락처(메일) 바꾸기 — 인증 기관에 반영한다(작업 UPID).</summary>
    [PveApi("PUT", "/cluster/acme/account/{name}")]
    public Task<string> UpdateAccountAsync(string name, string contact, CancellationToken ct = default)
    {
        return Api.PutActionAsync($"cluster/acme/account/{Seg(name)}",
            new Dictionary<string, string> { ["contact"] = contact }, ct);
    }

    /// <summary>계정 해지(작업 UPID).</summary>
    [PveApi("DELETE", "/cluster/acme/account/{name}")]
    public Task<string> DeactivateAccountAsync(string name, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"cluster/acme/account/{Seg(name)}", ct);
    }

    [PveApi("GET", "/cluster/acme/directories")]
    public Task<IReadOnlyList<Row>> DirectoriesAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/acme/directories", ct);
    }

    /// <summary>디렉터리의 약관 주소.</summary>
    [PveApi("GET", "/cluster/acme/tos")]
    public Task<string> TermsOfServiceAsync(string directory, CancellationToken ct = default)
    {
        return Api.GetTextAsync($"cluster/acme/tos?directory={Uri.EscapeDataString(directory)}", ct);
    }

    [PveApi("GET", "/cluster/acme/plugins")]
    public Task<IReadOnlyList<Row>> ListPluginsAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/acme/plugins", ct);
    }

    [PveApi("POST", "/cluster/acme/plugins")]
    public Task<string> CreatePluginAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PostActionAsync("cluster/acme/plugins", form, ct);
    }

    [PveApi("PUT", "/cluster/acme/plugins/{id}")]
    public Task<string> UpdatePluginAsync(string id, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"cluster/acme/plugins/{Seg(id)}", form, ct);
    }

    [PveApi("DELETE", "/cluster/acme/plugins/{id}")]
    public Task<string> DeletePluginAsync(string id, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"cluster/acme/plugins/{Seg(id)}", ct);
    }

    /// <summary>DNS 플러그인 종류와 입력 칸 스키마.</summary>
    [PveApi("GET", "/cluster/acme/challenge-schema")]
    public Task<IReadOnlyList<Row>> ChallengeSchemaAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/acme/challenge-schema", ct);
    }

    /// <summary>인증 기관 정보(8.1+, 보기 좋게 들여쓴 JSON) — 약관 주소·웹사이트·CAA 이름·외부 계정(EAB) 필요 여부.</summary>
    [PveApi("GET", "/cluster/acme/meta", Since = "8.1")]
    public async Task<string> DirectoryMetaJsonAsync(string directory, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetPrettyJsonAsync($"cluster/acme/meta?directory={Uri.EscapeDataString(directory)}", ct)
            .ConfigureAwait(false);
    }
}

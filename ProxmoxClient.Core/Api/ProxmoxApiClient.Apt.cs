using System.Text.Json;

namespace ProxmoxClient.Core.Api;

/// <summary>APT 저장소 목록 — 파일별 저장소 행과 Proxmox 표준 저장소, 수정 감지용 digest.</summary>
public sealed record AptRepositories(
    IReadOnlyList<IReadOnlyDictionary<string, string>> Repositories,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Standard,
    string Digest);

public sealed partial class ProxmoxApiClient
{
    /// <summary>
    ///     노드의 APT 저장소(GET nodes/{node}/apt/repositories). 파일 → 저장소 구조를 저장소마다 한 행으로 펼치며,
    ///     켜고 끌 때 필요한 파일 경로(path)와 파일 안 순번(index)을 함께 담는다.
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/apt/repositories")]
    public async Task<AptRepositories> GetAptRepositoriesAsync(string node, CancellationToken ct = default)
    {
        var data = await GetJsonAsync($"nodes/{Escape(node)}/apt/repositories", ct).ConfigureAwait(false);
        var digest = data.TryGetProperty("digest", out var d) ? ToText(d) : string.Empty;

        var repositories = new List<IReadOnlyDictionary<string, string>>();
        if (data.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            foreach (var file in files.EnumerateArray())
            {
                var path = file.TryGetProperty("path", out var p) ? ToText(p) : string.Empty;
                if (!file.TryGetProperty("repositories", out var repos) || repos.ValueKind != JsonValueKind.Array)
                    continue;

                var index = 0;
                foreach (var repo in repos.EnumerateArray())
                {
                    var row = ToStringMap(repo);
                    row["path"] = path;
                    row["index"] = index++.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    repositories.Add(row);
                }
            }

        var standard = new List<IReadOnlyDictionary<string, string>>();
        if (data.TryGetProperty("standard-repos", out var std) && std.ValueKind == JsonValueKind.Array)
            foreach (var repo in std.EnumerateArray())
                if (repo.ValueKind == JsonValueKind.Object)
                    standard.Add(ToStringMap(repo));

        return new AptRepositories(repositories, standard, digest);
    }
}

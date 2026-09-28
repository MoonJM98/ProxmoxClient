using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Storage;

/// <summary>
///     OCI 이미지를 CT 템플릿으로 받기(9.0+) — 저장소(예: docker.io/library/alpine)를 적고 태그를 목록에서 고른다.
/// </summary>
internal static class OciPullAction
{
    public static TableAction Create(ProxmoxApiClient api, string node, string storage)
    {
        return new TableAction
        {
            LabelKey = "OciPull_Action", IconKey = "IconDownload",
            Requires = api.Storage.Feature(nameof(StorageApi.OciPullAsync)),
            Run = (_, owner) => SubmitTaskAsync(api, owner, Loc.T("OciPull_Action"),
            [
                new FormField { Key = "repository", LabelKey = "OciPull_Repository", Required = true, Trim = true,
                    Hint = Loc.T("OciPull_RepositoryHint") },
                new FormField { Key = "tag", LabelKey = "OciPull_Tag", Trim = true, Hint = Loc.T("OciPull_TagHint"),
                    Suggest = values => TagsAsync(api, node, values) },
                new FormField { Key = "filename", LabelKey = "OciPull_FileName", Trim = true,
                    Hint = Loc.T("OciPull_FileNameHint") }
            ], values =>
            {
                var tag = values["tag"].Length > 0 ? values["tag"] : "latest";
                return api.Storage.OciPullAsync(node, storage, $"{values["repository"]}:{tag}", values["filename"]);
            }, "OciPull_Done")
        };
    }

    private static async Task<IReadOnlyList<(string, string)>> TagsAsync(ProxmoxApiClient api, string node,
        IReadOnlyDictionary<string, string> values)
    {
        var repository = values.TryGetValue("repository", out var r) ? r.Trim() : string.Empty;
        if (repository.Length == 0)
            throw new InvalidOperationException(Loc.T("StorageScan_NeedField", Loc.T("OciPull_Repository")));

        return (await api.Storage.OciTagsAsync(node, repository)).Select(tag => (tag, string.Empty)).ToList();
    }
}

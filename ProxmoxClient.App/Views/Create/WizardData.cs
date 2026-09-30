using ProxmoxClient.App.Views.Guest.Hardware;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Create;

/// <summary>
///     만들기 마법사가 고를 목록 — 노드마다 다르다(저장소·ISO·템플릿·브리지). 노드를 바꾸면 다시 읽는다.
///     목록 하나를 못 읽어도(권한 등) 나머지로 마법사는 열린다.
/// </summary>
internal sealed class WizardData
{
    public string NextId { get; private init; } = string.Empty;
    public IReadOnlyList<StorageInfo> ImageStorages { get; private init; } = [];
    public IReadOnlyList<StorageInfo> RootStorages { get; private init; } = [];
    public IReadOnlyList<(string Value, string Label)> Isos { get; private init; } = [];
    public IReadOnlyList<(string Value, string Label)> Templates { get; private init; } = [];
    public IReadOnlyList<(string Value, string Label)> Bridges { get; private init; } = [];
    public IReadOnlyList<(string Value, string Label)> Pools { get; private init; } = [];
    public IReadOnlyList<(string Value, string Label)> CpuModels { get; private init; } = [];
    public int HostCpus { get; private init; }
    public long HostMemoryMiB { get; private init; }

    public static async Task<WizardData> LoadAsync(ProxmoxApiClient api, string node)
    {
        var host = await TryAsync<Core.Models.PveNodeStatus?>(async () => await api.GetNodeStatusAsync(node), null);
        return new WizardData
        {
            NextId = (await TryAsync<int?>(() => api.GetNextVmIdAsync(), null))?.ToString() ?? string.Empty,
            ImageStorages = await StoragesAsync(api, node, "images"),
            RootStorages = await StoragesAsync(api, node, "rootdir"),
            Isos = await ContentAsync(api, node, "iso"),
            Templates = await ContentAsync(api, node, "vztmpl"),
            Bridges = (await TryAsync(() => api.GetNodeBridgesAsync(node), [])).Select(b => (b, b)).ToList(),
            Pools = (await TryAsync(() => api.Pools.ListAsync(), []))
                .Select(r => ActionHelpers.Value(r, "poolid")).Where(p => p.Length > 0).Select(p => (p, p)).ToList(),
            CpuModels = (await TryAsync(() => api.Guests.CpuModelsAsync(node), []))
                .Select(r => ActionHelpers.Value(r, "name")).Where(n => n.Length > 0)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => (n, n)).ToList(),
            HostCpus = host?.CpuCores ?? 0,
            HostMemoryMiB = (host?.MemTotalBytes ?? 0) / (1024 * 1024)
        };
    }

    /// <summary>저장소에 맞는 새 디스크 형식 — qcow2 를 지원하면 qcow2(웹 UI 기본), 아니면 저장소 기본(빈 값).</summary>
    public string FormatOf(string storage)
    {
        return ImageStorages.FirstOrDefault(s => s.Id == storage) is { SupportsQcow2: true } ? "qcow2" : string.Empty;
    }

    private static async Task<T> TryAsync<T>(Func<Task<T>> load, T fallback)
    {
        try
        {
            return await load();
        }
        catch (ProxmoxApiException ex)
        {
            App.Log($"[만들기] 목록 조회 실패: {ex.Message}");
            return fallback;
        }
    }

    private static async Task<IReadOnlyList<StorageInfo>> StoragesAsync(ProxmoxApiClient api, string node,
        string content)
    {
        var rows = await TryAsync(() => api.Storage.NodeStoragesAsync(node, content), []);
        return rows.Select(r => new StorageInfo(ActionHelpers.Value(r, "storage"), ActionHelpers.Value(r, "type")))
            .Where(s => s.Id.Length > 0).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>이 콘텐츠를 담는 모든 저장소의 파일(볼륨 ID, 파일 이름).</summary>
    private static async Task<IReadOnlyList<(string, string)>> ContentAsync(ProxmoxApiClient api, string node,
        string content)
    {
        var result = new List<(string, string)>();
        foreach (var storage in await StoragesAsync(api, node, content))
            result.AddRange((await TryAsync(() => api.GetStorageContentAsync(node, storage.Id, content), []))
                .Select(f => (f.Volid, f.Volid)));
        return result;
    }

    /// <summary>
    ///     만들기 요청을 보내고 작업이 끝날 때까지 기다린다 — 작업이 실패하면 예외(마법사 창은 열린 채 상태 줄에 남는다).
    /// </summary>
    public static async Task<string> CreateAsync(ProxmoxApiClient api, string node, ResourceKind kind,
        IReadOnlyDictionary<string, string> values, string id)
    {
        var upid = await api.Guests.CreateAsync(node, kind, values);
        var status = upid.Length == 0 ? "OK" : (await api.WaitTaskAsync(upid)).Status;
        // "WARNINGS: n" 은 끝까지 성공한 작업이다(게스트는 만들어졌다)
        if (status != "OK" && !status.StartsWith("WARNINGS", StringComparison.Ordinal))
            throw new InvalidOperationException(status);
        return Loc.T("Wz_Created", id);
    }

    public static IReadOnlyList<(string, string)> StorageChoices(IReadOnlyList<StorageInfo> storages)
    {
        return storages.Select(s => (s.Id, $"{s.Id} ({s.Type})")).ToList();
    }
}

using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>
///     하드웨어 편집 창 하나 — 칸 목록과, 입력 결과를 바꿀 설정들로 모으는 함수.
///     바꿀 설정의 빈 값은 삭제(서버 기본값)를 뜻한다.
/// </summary>
internal sealed class HardwareEdit
{
    public required string Title { get; init; }
    public required IReadOnlyList<FormField> Fields { get; init; }
    public required Func<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>> Build { get; init; }
    public Func<IReadOnlyDictionary<string, string>, string?>? Validate { get; init; }

    /// <summary>
    ///     웹 UI 가 POST + background_delay=5 로 보내는 편집(디스크·CD·EFI) — 서버 작업(UPID)이 생길 수 있어 끝날 때까지 기다린다.
    /// </summary>
    public bool Background { get; init; }
}

/// <summary>저장소 하나 — id 와 종류(파일 기반이면 qcow2 를 쓸 수 있다).</summary>
internal sealed record StorageInfo(string Id, string Type)
{
    /// <summary>웹 UI 는 qcow2 를 지원하는 저장소에서 qcow2 를 기본으로 고른다.</summary>
    public bool SupportsQcow2 => Type is "dir" or "nfs" or "cifs" or "glusterfs";

    public bool IsFileBased => SupportsQcow2 || Type == "btrfs";
}

/// <summary>하드웨어 편집기가 쓰는 공통 정보 — API·게스트·현재 설정과 노드 목록 조회.</summary>
internal sealed class HardwareContext(ProxmoxApiClient api, PveResource guest, GuestPendingConfig config, bool running)
{
    public ProxmoxApiClient Api { get; } = api;
    public PveResource Guest { get; } = guest;
    public GuestPendingConfig Config { get; } = config;
    public bool IsRunning { get; } = running;

    /// <summary>대기 중 변경까지 반영한 설정 — 편집은 이 값에서 시작한다(웹 UI 의 config 조회와 같다).</summary>
    public IReadOnlyDictionary<string, string> Effective => Config.Effective;


    public string Get(string key)
    {
        return Effective.TryGetValue(key, out var v) ? v : string.Empty;
    }

    /// <summary>컨테이너(LXC) 리소스 화면인가.</summary>
    public bool IsCt => Guest.Kind == ResourceKind.Lxc;

    public bool IsWindows => Get("ostype") is "w2k" or "wxp" or "w2k8" or "win7" or "win8" or "win10" or "win11";

    /// <summary>이 노드에서 쓸 수 있고 켜진 저장소(content 로 거른다).</summary>
    public async Task<IReadOnlyList<StorageInfo>> StoragesAsync(string content)
    {
        var rows = await Api.Storage.NodeStoragesAsync(Guest.Node, content);
        return rows.Select(r => new StorageInfo(ActionHelpers.Value(r, "storage"), ActionHelpers.Value(r, "type")))
            .Where(s => s.Id.Length > 0)
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>ISO 이미지 전체(저장소:iso/이름) — 웹 UI 의 저장소·ISO 두 칸을 한 목록으로 합친다.</summary>
    public async Task<IReadOnlyList<(string Value, string Label)>> IsoChoicesAsync()
    {
        var result = new List<(string, string)>();
        foreach (var storage in await StoragesAsync("iso"))
            result.AddRange((await Api.GetStorageContentAsync(Guest.Node, storage.Id, "iso"))
                .Select(f => (f.Volid, f.Volid)));
        return result;
    }

    public async Task<IReadOnlyList<(string Value, string Label)>> BridgeChoicesAsync()
    {
        return (await Api.GetNodeBridgesAsync(Guest.Node)).Select(b => (b, b)).ToList();
    }

    /// <summary>노드의 CPU·메모리 — 최대치 안내용(못 읽으면 null).</summary>
    public async Task<PveNodeStatus?> HostAsync()
    {
        try
        {
            return await Api.GetNodeStatusAsync(Guest.Node);
        }
        catch (ProxmoxApiException ex)
        {
            App.Log($"[하드웨어] 노드 {Guest.Node} 자원 조회 실패: {ex.Message}");
            return null;
        }
    }

    /// <summary>버스에서 비어 있는(현재·대기 중 모두) 가장 작은 번호의 키. 없으면 null.</summary>
    public string? FreeSlot(string bus, int max)
    {
        for (var i = 0; i < max; i++)
        {
            var key = $"{bus}{i}";
            if (!Config.Current.ContainsKey(key) && !Effective.ContainsKey(key)) return key;
        }

        return null;
    }

    /// <summary>이 종류 장치 수(현재·대기 중 합친 키 기준).</summary>
    public int Count(string bus)
    {
        return Config.Current.Keys.Concat(Effective.Keys).Distinct()
            .Count(k => k.StartsWith(bus, StringComparison.Ordinal) && k.Length > bus.Length
                                                                  && char.IsDigit(k[bus.Length]));
    }
}

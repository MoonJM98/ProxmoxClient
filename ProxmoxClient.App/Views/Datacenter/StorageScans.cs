using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     저장소 추가 창의 찾아보기(웹 UI 의 scan 목록) — 유형·칸마다 서버에 물어볼 요청과, 결과에서 값·설명으로 쓸 열.
///     앞 칸(서버 주소·계정·볼륨 그룹)이 비어 있으면 먼저 채우라고 알린다.
/// </summary>
internal static class StorageScans
{
    private delegate Task<IReadOnlyList<Row>> Scan(ProxmoxApiClient api, Func<string, string> value);

    private sealed record Scanner(Scan Run, string ValueColumn, string? DescriptionColumn, string[] Needs);

    private static readonly Dictionary<(string Type, string Key), Scanner> Scanners = new()
    {
        [("nfs", "export")] = new Scanner((api, v) => api.Storage.ScanNfsAsync(v("server")),
            "path", "options", ["server"]),
        [("cifs", "share")] = new Scanner(
            (api, v) => api.Storage.ScanCifsAsync(v("server"), v("username"), v("password"), v("domain")),
            "share", "description", ["server"]),
        [("pbs", "datastore")] = new Scanner(
            (api, v) => api.Storage.ScanPbsAsync(v("server"), v("username"), v("password"), v("fingerprint"),
                v("port")),
            "store", "comment", ["server", "username", "password"]),
        [("iscsi", "target")] = new Scanner((api, v) => api.Storage.ScanIscsiAsync(v("portal")),
            "target", "portal", ["portal"]),
        [("zfs", "target")] = new Scanner((api, v) => api.Storage.ScanIscsiAsync(v("portal")),
            "target", "portal", ["portal"]),
        [("lvm", "vgname")] = new Scanner((api, _) => api.Storage.ScanLvmAsync(), "vg", null, []),
        [("lvmthin", "vgname")] = new Scanner((api, _) => api.Storage.ScanLvmAsync(), "vg", null, []),
        [("lvmthin", "thinpool")] = new Scanner((api, v) => api.Storage.ScanLvmThinAsync(v("vgname")),
            "lv", null, ["vgname"]),
        [("zfspool", "pool")] = new Scanner((api, _) => api.Storage.ScanZfsAsync(), "pool", null, [])
    };

    /// <summary>이 칸을 서버에서 찾아볼 수 있으면 그 함수(없으면 null — 그냥 입력 칸).</summary>
    public static Func<IReadOnlyDictionary<string, string>, Task<IReadOnlyList<(string, string)>>>? For(
        ProxmoxApiClient api, StorageType type, IReadOnlyList<StorageField> fields, string key)
    {
        if (!Scanners.TryGetValue((type.Type, key), out var scanner)) return null;

        return async values =>
        {
            string V(string k) => values.TryGetValue(k, out var v) ? v : string.Empty;
            if (scanner.Needs.FirstOrDefault(k => V(k).Length == 0) is { } missing)
            {
                var label = fields.FirstOrDefault(f => f.Field.Key == missing)?.Field.LabelKey ?? missing;
                throw new InvalidOperationException(Loc.T("StorageScan_NeedField", Loc.T(label)));
            }

            var rows = await scanner.Run(api, V);
            return rows.Select(r => (Value(r, scanner.ValueColumn),
                    scanner.DescriptionColumn is { } d ? Value(r, d) : string.Empty))
                .Where(r => r.Item1.Length > 0)
                .DistinctBy(r => r.Item1, StringComparer.Ordinal)
                .ToList();
        };
    }

    private static string Value(Row row, string key)
    {
        return row.TryGetValue(key, out var v) ? v : string.Empty;
    }
}

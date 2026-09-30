using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Node;

/// <summary>Ceph 클러스터 플래그(noout 등)와 풀 설정 바꾸기 — 웹 UI 의 Ceph → OSD '전역 플래그'와 풀 편집.</summary>
internal static class CephExtras
{
    private static readonly IReadOnlyList<TableColumn> FlagColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 130 },
        new() { Key = "value", HeaderKey = "CephFlags_Set", Width = 70, Format = TableFormats.Flag },
        new() { Key = "description", HeaderKey = "Table_Description", Width = 0 }
    ];

    /// <summary>풀 편집 칸 — 서버 필드 이름, 목록 행에서 읽을 열, 표시 이름.</summary>
    private static readonly (string Key, string LabelKey)[] PoolKeys =
    [
        ("size", "CephTab_Size"), ("min_size", "CephTab_MinSize"), ("pg_num", "CephTab_PgNum"),
        ("target_size_ratio", "CephPool_TargetRatio")
    ];

    /// <summary>
    ///     Ceph 데몬 시작·중지·재시작 — 고른 행의 데몬(mon.이름·mgr.이름·osd.번호)을 그 데몬이 있는 노드에 요청한다.
    ///     중지·재시작은 데이터 재배치나 쿼럼에 영향을 주므로 확인을 받는다.
    /// </summary>
    public static IReadOnlyList<TableAction> DaemonControls(ProxmoxApiClient api, string node,
        Func<IReadOnlyDictionary<string, string>, string> serviceOf)
    {
        TableAction Control(string verb, string labelKey, string iconKey, bool confirm) => new()
        {
            LabelKey = labelKey, IconKey = iconKey, NeedsSelection = true,
            Confirm = confirm ? row => Loc.T("CephDaemon_Confirm", Loc.T(labelKey), serviceOf(row!)) : null,
            Run = async (row, owner) =>
            {
                var host = CephManage.HostOf(row!, node);
                var service = serviceOf(row!);
                if (verb == "stop" && !await ConfirmSafeAsync(api, host, service, "stop", owner)) return null;
                return await RunTaskAsync(api, api.Ceph.ServiceCommandAsync(host, verb, service), "CephDaemon_Done");
            }
        };

        return
        [
            Control("start", "NodeServices_Start", "IconPlay", confirm: false),
            Control("stop", "NodeServices_Stop", "IconSquare", confirm: true),
            Control("restart", "NodeServices_Restart", "IconRotate", confirm: true)
        ];
    }

    /// <summary>
    ///     멈추거나 없애기 전 Ceph 에 안전한지 묻는다(7.2+) — 안전하지 않으면 이유를 보여 주고 한 번 더 확인한다.
    ///     service 는 "osd.3"·"mon.pve" 꼴이며, 확인할 수 없는 데몬(mgr)이나 옛 서버는 묻지 않고 진행한다.
    /// </summary>
    internal static async Task<bool> ConfirmSafeAsync(ProxmoxApiClient api, string host, string service,
        string action, Window? owner)
    {
        var dot = service.IndexOf('.');
        var type = dot > 0 ? service[..dot] : service;
        if (dot <= 0 || type is not ("osd" or "mon" or "mds")
            || !api.Ceph.Feature(nameof(Core.Api.Domains.CephApi.CmdSafetyAsync)).IsAvailable)
            return true;

        string reason;
        try
        {
            var check = await api.Ceph.CmdSafetyAsync(host, type, service[(dot + 1)..], action);
            if (Value(check, "safe") is "1" or "true") return true;
            reason = Value(check, "status");
        }
        catch (ProxmoxApiException ex)
        {
            reason = ex.Message; // 확인 자체가 실패해도 사용자가 판단하게 한다
        }

        var answer = ThemedMessageBox.Show(owner ?? Application.Current.MainWindow!,
            Loc.T("CephSafety_Unsafe", service, reason), Loc.T("TableTab_ConfirmTitle"), MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes;
    }

    /// <summary>OSD 자세한 정보(7.4+) — 장치·BlueStore·메모리 등 서버가 주는 값을 그대로.</summary>
    public static TableAction OsdDetails(ProxmoxApiClient api, string node)
    {
        return new TableAction
        {
            LabelKey = "CephOsd_Details", IconKey = "IconList", NeedsSelection = true,
            Requires = api.Ceph.Feature(nameof(Core.Api.Domains.CephApi.OsdMetadataJsonAsync)),
            Run = async (row, owner) => TextViewWindow.ShowModal(owner,
                Loc.T("CephOsd_DetailsTitle", Value(row!, "name")),
                await api.Ceph.OsdMetadataJsonAsync(CephManage.HostOf(row!, node), Value(row!, "id")))
        };
    }

    internal static readonly IReadOnlyList<TableColumn> ConfigDbColumns =
    [
        new() { Key = "section", HeaderKey = "CephCfg_Section", Width = 90 },
        new() { Key = "name", HeaderKey = "Table_Name", Width = 220 },
        new() { Key = "value", HeaderKey = "NodeSubscription_Value", Width = 200 },
        new() { Key = "level", HeaderKey = "CephCfg_Level", Width = 90 },
        new() { Key = "can_update_at_runtime", HeaderKey = "CephCfg_Runtime", Width = 0, Format = TableFormats.Flag }
    ];

    /// <summary>풀 상태(7.4+) — 사용량·자동 조정 등 서버가 주는 값을 그대로.</summary>
    public static TableAction PoolStatus(ProxmoxApiClient api, string node)
    {
        return new TableAction
        {
            LabelKey = "CephPool_Status", IconKey = "IconList", NeedsSelection = true,
            Requires = api.Ceph.Feature(nameof(Core.Api.Domains.CephApi.PoolStatusJsonAsync)),
            Run = async (row, owner) => TextViewWindow.ShowModal(owner,
                Loc.T("CephPool_StatusTitle", row!["pool_name"]),
                await api.Ceph.PoolStatusJsonAsync(node, row["pool_name"]))
        };
    }

    /// <summary>OSD 의 LVM 볼륨 정보(7.4+).</summary>
    public static TableAction OsdLvInfo(ProxmoxApiClient api, string node)
    {
        return new TableAction
        {
            LabelKey = "CephOsd_LvInfo", IconKey = "IconDatabase", NeedsSelection = true,
            Requires = api.Ceph.Feature(nameof(Core.Api.Domains.CephApi.OsdLvInfoJsonAsync)),
            Run = async (row, owner) => TextViewWindow.ShowModal(owner,
                Loc.T("CephOsd_LvInfoTitle", Value(row!, "name")),
                await api.Ceph.OsdLvInfoJsonAsync(CephManage.HostOf(row!, node), Value(row!, "id")))
        };
    }

    /// <summary>클러스터 전체 플래그 — 편집 권한이면 플래그 변경과 클러스터 순차 재시작·릴리스 보기(9.2+)도 둔다.</summary>
    public static TableTab Flags(ProxmoxApiClient api, string node, bool canEdit)
    {
        return new TableTab(() => CephTabs.Guard(() => api.Ceph.FlagsAsync()), FlagColumns, "CephFlags_Hint",
            canEdit
                ?
                [
                    new TableAction
                    {
                        LabelKey = "CephFlags_Change", IconKey = "IconPencil",
                        Run = (_, owner) => ChangeFlagsAsync(api, owner)
                    },
                    ..CephMaintenance.ClusterActions(api, node)
                ]
                : null);
    }

    private static async Task<string?> ChangeFlagsAsync(ProxmoxApiClient api, Window? owner)
    {
        var flags = await api.Ceph.FlagsAsync();
        var names = flags.Select(f => Value(f, "name")).Where(n => n.Length > 0).ToList();
        var on = flags.Where(f => Value(f, "value") is "1" or "true").Select(f => Value(f, "name")).ToList();
        return await SubmitTaskAsync(api, owner, Loc.T("CephFlags_Title"),
        [
            new FormField
            {
                Key = "flags", LabelKey = "CephFlags_Set", Kind = FormFieldKind.MultiChoice,
                Choices = names.Select(n => (n, n)).ToList(), Initial = string.Join(",", on),
                Hint = Loc.T("CephFlags_ChangeHint")
            }
        ], values =>
        {
            // 창을 연 뒤 다른 관리자가 바꾼 플래그를 되돌리지 않도록 이 창에서 바꾼 것만 보낸다
            var chosen = values["flags"].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var before = on.ToHashSet();
            var changed = names.Where(n => chosen.Contains(n) != before.Contains(n))
                .ToDictionary(n => n, n => chosen.Contains(n) ? "1" : "0");
            return changed.Count == 0 ? Task.FromResult(string.Empty) : api.Ceph.SetFlagsAsync(changed);
        }, "CephFlags_Changed");
    }

    /// <summary>풀 편집(7.4 전은 옛 경로) — 바꾼 칸만 보낸다.</summary>
    public static TableAction EditPool(ProxmoxApiClient api, string node)
    {
        return new TableAction
        {
            LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
            Run = (row, owner) =>
            {
                var name = row!["pool_name"];
                var fields = PoolKeys.Select(k => new FormField
                    {
                        Key = k.Key, LabelKey = k.LabelKey, Initial = Value(row, k.Key), Trim = true
                    })
                    .ToList();
                if (Value(row, "pg_autoscale_mode").Length > 0)
                    fields.Add(new FormField
                    {
                        Key = "pg_autoscale_mode", LabelKey = "CephTab_Autoscale", Kind = FormFieldKind.Choice,
                        Initial = Value(row, "pg_autoscale_mode"),
                        Choices = [("on", "on"), ("warn", "warn"), ("off", "off")]
                    });
                return SubmitTaskAsync(api, owner, Loc.T("CephPool_EditTitle", name), fields,
                    values => Changed(values, row) is { Count: > 0 } changed
                        ? api.Ceph.UpdatePoolAsync(node, name, changed)
                        : Task.FromResult(string.Empty), "CephPool_Updated",
                    ValidatePool);
            }
        };
    }

    private static Dictionary<string, string> Changed(IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> row)
    {
        return values.Where(kv => kv.Value.Length > 0 && kv.Value != Value(row, kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private static string? ValidatePool(IReadOnlyDictionary<string, string> values)
    {
        foreach (var key in new[] { "size", "min_size", "pg_num" })
            if (values[key].Length > 0 && !(int.TryParse(values[key], out var n) && n > 0))
                return Loc.T("CephPool_BadNumber", key);
        if (values["target_size_ratio"].Length > 0
            && !double.TryParse(values["target_size_ratio"], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return Loc.T("CephPool_BadNumber", "target_size_ratio");
        if (int.TryParse(values["size"], out var size) && int.TryParse(values["min_size"], out var min) && min > size)
            return Loc.T("CephPool_MinOverSize");
        return null;
    }
}

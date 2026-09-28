using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Node;

/// <summary>
///     Ceph 유지 보수(9.2+) — 상태 경고 음소거, 데몬 순차 재시작(업그레이드 뒤 오래된 데몬만 등), 설치할 수 있는 릴리스,
///     CephFS 삭제. 서버가 9.2 보다 낮으면 버튼·탭이 저절로 빠진다.
/// </summary>
internal static class CephMaintenance
{
    private static readonly IReadOnlyList<TableColumn> MuteColumns =
    [
        new() { Key = "code", HeaderKey = "CephMute_Code", Width = 200 },
        new() { Key = "summary", HeaderKey = "Table_Description", Width = 0 },
        new() { Key = "ttl", HeaderKey = "CephMute_Ttl", Width = 110 },
        new() { Key = "sticky", HeaderKey = "CephMute_Sticky", Width = 70, Format = TableFormats.Flag }
    ];

    private static readonly IReadOnlyList<TableColumn> ReleaseColumns =
    [
        new() { Key = "release", HeaderKey = "Table_Name", Width = 120 },
        new() { Key = "version", HeaderKey = "NodeApt_Version", Width = 120 },
        new() { Key = "available", HeaderKey = "CephRelease_Available", Width = 80, Format = TableFormats.Flag },
        new() { Key = "is-default", HeaderKey = "CephRelease_Default", Width = 80, Format = TableFormats.Flag },
        new() { Key = "unsupported", HeaderKey = "CephRelease_Unsupported", Width = 0, Format = TableFormats.Flag }
    ];

    public static SubTab MutesTab(ProxmoxApiClient api, bool canEdit)
    {
        return new SubTab("CephMute_Tab", () => new TableTab(() => CephTabs.Guard(() => api.Ceph.HealthMutesAsync()),
                MuteColumns, "CephMute_Hint", canEdit ? MuteActions(api) : null),
            api.Ceph.Feature(nameof(CephApi.HealthMutesAsync)));
    }

    private static IReadOnlyList<TableAction> MuteActions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "CephMute_Add", IconKey = "IconPause",
                Run = (_, owner) => SubmitTaskAsync(api, owner, Loc.T("CephMute_Add"),
                [
                    new FormField { Key = "code", LabelKey = "CephMute_Code", Required = true, Trim = true,
                        Hint = Loc.T("CephMute_CodeHint") },
                    new FormField
                    {
                        Key = "ttl", LabelKey = "CephMute_Ttl", Trim = true, Hint = Loc.T("CephMute_TtlHint")
                    },
                    new FormField { Key = "sticky", LabelKey = "CephMute_Sticky", Kind = FormFieldKind.Bool }
                ], values => api.Ceph.SetHealthMuteAsync(values["code"], true, values["ttl"], values["sticky"] == "1"),
                    "CephMute_Done")
            },
            new TableAction
            {
                LabelKey = "CephMute_Remove", IconKey = "IconPlay", NeedsSelection = true,
                Run = async (row, _) => await RunTaskAsync(api,
                    api.Ceph.SetHealthMuteAsync(row!["code"], false), "CephMute_Done")
            }
        ];
    }

    /// <summary>클러스터 전체 데몬 순차 재시작과 릴리스 보기 — 플래그 탭(클러스터 단위)에 둔다.</summary>
    public static IReadOnlyList<TableAction> ClusterActions(ProxmoxApiClient api, string node)
    {
        return
        [
            new TableAction
            {
                LabelKey = "CephRestart_Cluster", IconKey = "IconRotate",
                Requires = api.Ceph.Feature(nameof(CephApi.ClusterRestartBulkAsync)),
                Run = (_, owner) => SubmitTaskAsync(api, owner, Loc.T("CephRestart_Cluster"),
                [
                    new FormField
                    {
                        Key = "service-type", LabelKey = "Table_Type", Kind = FormFieldKind.Choice, Initial = "osd",
                        Choices = [("mon", "mon"), ("mgr", "mgr"), ("mds", "mds"), ("osd", "osd")]
                    },
                    ..RestartOptions()
                ], values => api.Ceph.ClusterRestartBulkAsync(NonEmpty(values)), "CephRestart_Done")
            },
            new TableAction
            {
                LabelKey = "CephMeta_Show", IconKey = "IconList",
                Run = async (_, owner) => TextViewWindow.ShowModal(owner, Loc.T("CephMeta_Show"),
                    await api.Ceph.ClusterMetadataJsonAsync())
            },
            new TableAction
            {
                LabelKey = "CephRelease_Show", IconKey = "IconBox",
                Requires = api.Ceph.Feature(nameof(CephApi.ReleasesAsync)),
                Run = (_, owner) => Task.FromResult(TableWindow.ShowModal(owner, Loc.T("CephRelease_Show"),
                    new TableTab(() => api.Ceph.ReleasesAsync(node), ReleaseColumns, "CephRelease_Hint")))
            }
        ];
    }

    /// <summary>이 노드 OSD 순차 재시작 — noout 을 잠시 켜 두면 재시작 중 데이터가 옮겨 다니지 않는다.</summary>
    public static TableAction NodeOsdRestart(ProxmoxApiClient api, string node)
    {
        return new TableAction
        {
            LabelKey = "CephRestart_NodeOsd", IconKey = "IconRotate",
            Requires = api.Ceph.Feature(nameof(CephApi.NodeRestartBulkAsync)),
            Run = (_, owner) => SubmitTaskAsync(api, owner, Loc.T("CephRestart_NodeOsd"),
            [
                new FormField { Key = "set-noout", LabelKey = "CephRestart_SetNoout", Kind = FormFieldKind.Bool,
                    Initial = "1" },
                ..RestartOptions()
            ], values => api.Ceph.NodeRestartBulkAsync(node, NonEmpty(values)), "CephRestart_Done")
        };
    }

    private static IEnumerable<FormField> RestartOptions()
    {
        yield return new FormField { Key = "only-outdated", LabelKey = "CephRestart_OnlyOutdated",
            Kind = FormFieldKind.Bool, Initial = "1" };
        yield return new FormField { Key = "dry-run", LabelKey = "CephRestart_DryRun", Kind = FormFieldKind.Bool,
            Hint = Loc.T("CephRestart_DryRunHint") };
        yield return new FormField { Key = "timeout", LabelKey = "CephRestart_Timeout", Trim = true, Advanced = true,
            Hint = Loc.T("CephRestart_TimeoutHint") };
    }

    /// <summary>CephFS 삭제(9.2+) — 이름을 그대로 입력해야 지운다. 풀·PVE 저장소도 함께 지울지 고른다.</summary>
    public static TableAction DeleteFileSystem(ProxmoxApiClient api, string node)
    {
        return new TableAction
        {
            LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
            Requires = api.Ceph.Feature(nameof(CephApi.DeleteFileSystemAsync)),
            Run = (row, owner) => DeleteFileSystemAsync(api, node, Value(row!, "name"), owner)
        };
    }

    private static Task<string?> DeleteFileSystemAsync(ProxmoxApiClient api, string node, string name,
        Window? owner)
    {
        return SubmitTaskAsync(api, owner, Loc.T("CephFs_DeleteTitle", name),
        [
            new FormField { Key = "remove-pools", LabelKey = "CephFs_RemovePools", Kind = FormFieldKind.Bool },
            new FormField { Key = "remove-storages", LabelKey = "NodeDisks_CleanupConfig", Kind = FormFieldKind.Bool },
            TypeToConfirmField()
        ], values => api.Ceph.DeleteFileSystemAsync(node, name, values["remove-pools"] == "1",
            values["remove-storages"] == "1"), "CephFs_Deleted", TypedMatches(name));
    }
}

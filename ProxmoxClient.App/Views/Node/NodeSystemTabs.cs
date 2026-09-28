using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Guest.Tabs;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Node;

/// <summary>노드의 시스템 화면들 — 옵션, hosts 파일, 구독, 복제 상태.</summary>
internal static class NodeSystemTabs
{
    private static readonly GuestOption[] Options =
    [
        new()
        {
            Key = "startall-onboot-delay", LabelKey = "NodeOptions_StartDelay", Kind = OptionKind.Text,
            EmptyLabelKey = "DcOptions_Default"
        },
        new()
        {
            Key = "wakeonlan", LabelKey = "NodeOptions_WakeOnLan", Kind = OptionKind.Text,
            EmptyLabelKey = "GuestOptions_NotSet"
        },
        new()
        {
            Key = "ballooning-target", LabelKey = "NodeOptions_BallooningTarget", Kind = OptionKind.Text,
            EmptyLabelKey = "DcOptions_Default"
        }
    ];

    private static readonly IReadOnlyList<TableColumn> KeyValueColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 180 },
        new() { Key = "value", HeaderKey = "NodeSubscription_Value", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> ReplicationColumns =
    [
        new() { Key = "id", HeaderKey = "Table_Id", Width = 90 },
        new() { Key = "guest", HeaderKey = "Table_Guest", Width = 70 },
        new() { Key = "target", HeaderKey = "Table_Target", Width = 100 },
        new()
        {
            Key = "last_sync", HeaderKey = "NodeReplication_LastSync", Width = 140, Format = TableFormats.EpochDate
        },
        new() { Key = "next_sync", HeaderKey = "Table_NextRun", Width = 140, Format = TableFormats.EpochDate },
        new() { Key = "duration", HeaderKey = "NodeReplication_Duration", Width = 80 },
        new() { Key = "fail_count", HeaderKey = "NodeReplication_Failures", Width = 60 },
        new() { Key = "error", HeaderKey = "NodeReplication_Error", Width = 0 }
    ];

    /// <summary>표시 순서와 이름 — 구독 정보의 나머지 필드(서명 등)는 보여 주지 않는다.</summary>
    private static readonly (string Key, string LabelKey)[] SubscriptionFields =
    [
        ("productname", "NodeSubscription_Product"), ("status", "Table_State"), ("key", "NodeSubscription_Key"),
        ("serverid", "NodeSubscription_ServerId"), ("sockets", "NodeSubscription_Sockets"),
        ("level", "NodeSubscription_Level"), ("regdate", "NodeSubscription_RegDate"),
        ("nextduedate", "NodeSubscription_DueDate"), ("checktime", "NodeSubscription_CheckTime"),
        ("message", "NodeSubscription_Message")
    ];

    public static OptionsTab NodeOptions(ProxmoxApiClient api, string node)
    {
        // 서버가 모르는 설정(예: 8.3 전의 ballooning-target)은 옵션 목록이 저장 요청(target)을 보고 뺀다
        return new OptionsTab(Options, () => api.Nodes.GetConfigAsync(node),
            async changes => await api.Nodes.UpdateConfigAsync(node, UpdateForm(changes)),
            api.Nodes.Feature(nameof(NodesApi.UpdateConfigAsync)));
    }

    public static TextEditTab Hosts(ProxmoxApiClient api, string node, bool canEdit)
    {
        return new TextEditTab(async () =>
            {
                var hosts = await api.Nodes.GetHostsAsync(node);
                return (Value(hosts, "data"), Value(hosts, "digest"));
            },
            canEdit
                ? async (text, digest) => await api.Nodes.SetHostsAsync(node, text, digest)
                : null,
            "NodeHosts_Hint");
    }

    public static TableTab Subscription(ProxmoxApiClient api, string node, bool canEdit)
    {
        IReadOnlyList<TableAction>? actions = canEdit
            ?
            [
                new TableAction
                {
                    LabelKey = "NodeSubscription_Upload", IconKey = "IconPencil",
                    Run = (_, owner) => SubmitAsync(owner, "NodeSubscription_Upload",
                        [new FormField { Key = "key", LabelKey = "NodeSubscription_Key", Required = true }],
                        values => api.Nodes.SetSubscriptionKeyAsync(node, values["key"]), "NodeSubscription_Uploaded")
                },
                new TableAction
                {
                    LabelKey = "NodeSubscription_Check", IconKey = "IconCheck",
                    Run = async (_, _) =>
                    {
                        await api.Nodes.CheckSubscriptionAsync(node);
                        return Loc.T("NodeSubscription_Checked");
                    }
                },
                new TableAction
                {
                    LabelKey = "NodeSubscription_Remove", IconKey = "IconTrash",
                    Confirm = _ => Loc.T("NodeSubscription_RemoveConfirm"),
                    Run = async (_, _) =>
                    {
                        await api.Nodes.DeleteSubscriptionAsync(node);
                        return Loc.T("NodeSubscription_Removed");
                    }
                }
            ]
            : null;

        return new TableTab(async () =>
        {
            var info = await api.Nodes.GetSubscriptionAsync(node);
            return SubscriptionFields
                .Where(f => Value(info, f.Key).Length > 0)
                .Select(f => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["name"] = Loc.T(f.LabelKey), ["value"] = Value(info, f.Key)
                })
                .ToList();
        }, KeyValueColumns, "NodeSubscription_Hint", actions);
    }

    /// <summary>
    ///     복제 상태 표. guest 를 주면 그 게스트의 작업만 보이고, 작업 추가·삭제 버튼도 붙는다(게스트 창의 복제 탭).
    /// </summary>
    public static TableTab Replication(ProxmoxApiClient api, string node, int? guest = null, bool canEdit = false)
    {
        var actions = new List<TableAction>
        {
            new()
            {
                LabelKey = "NodeReplication_RunNow", IconKey = "IconPlay", NeedsSelection = true,
                Run = async (row, _) =>
                {
                    await api.Nodes.RunReplicationNowAsync(node, row!["id"]);
                    return Loc.T("NodeReplication_Scheduled");
                }
            },
            new()
            {
                LabelKey = "NodeReplication_Log", IconKey = "IconList", NeedsSelection = true,
                Run = async (row, owner) =>
                {
                    var lines = await api.Nodes.ReplicationLogAsync(node, row!["id"]);
                    return TextViewWindow.ShowModal(owner, Loc.T("NodeReplication_LogTitle", row["id"]),
                        string.Join(Environment.NewLine, lines.Select(l => Value(l, "t"))));
                }
            }
        };

        if (guest is { } id && canEdit)
        {
            actions.Insert(0, new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => AddGuestReplicationAsync(api, node, id, owner)
            });
            actions.Add(DeleteAction(row => Loc.T("DcReplication_DeleteConfirm", row["id"]),
                row => api.Jobs.DeleteReplicationAsync(row["id"]), "DcReplication_Deleted"));
        }

        return new TableTab(() => api.Nodes.ReplicationAsync(node, guest), ReplicationColumns, "NodeReplication_Hint",
            actions);
    }

    private static async Task<string?> AddGuestReplicationAsync(ProxmoxApiClient api, string node, int guest,
        System.Windows.Window? owner)
    {
        var targets = (await api.GetNodesAsync())
            .Where(n => n.Node != node)
            .Select(n => (n.Node, n.Node))
            .ToList();
        var existing = await api.Jobs.ListReplicationAsync();
        var vmid = guest.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return await SubmitAsync(owner, "DcReplication_AddTitle",
        [
            new FormField
            {
                Key = "target", LabelKey = "Table_Target", Kind = FormFieldKind.Choice, Choices = targets,
                Required = true
            },
            new FormField { Key = "schedule", LabelKey = "Table_Schedule", Initial = "*/15" },
            new FormField { Key = "rate", LabelKey = "DcReplication_Rate" },
            new FormField { Key = "comment", LabelKey = "Table_Comment" }
        ], values =>
        {
            var form = NonEmpty(values);
            form["id"] = Datacenter.ClusterActions.NextReplicationId(existing, vmid);
            form["type"] = "local";
            return api.Jobs.CreateReplicationAsync(form);
        }, "DcReplication_Added");
    }
}

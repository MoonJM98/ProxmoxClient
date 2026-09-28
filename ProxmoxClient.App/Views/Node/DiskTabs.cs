using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Node;

/// <summary>
///     노드 디스크 화면 — 웹 UI 처럼 디스크·LVM·LVM-Thin·디렉터리·ZFS 하위 탭.
///     디스크를 지우거나 저장소를 없애는 작업은 대상 이름을 직접 입력해야 실행된다.
/// </summary>
internal static class DiskTabs
{
    private static readonly IReadOnlyList<TableColumn> DiskColumns =
    [
        new() { Key = "devpath", HeaderKey = "Table_Device", Width = 110 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 60 },
        new() { Key = "used", HeaderKey = "Table_Usage", Width = 110 },
        new() { Key = "size", HeaderKey = "Table_Size", Width = 90, Format = TableFormats.Bytes },
        new() { Key = "gpt", HeaderKey = "Table_Gpt", Width = 50, Format = TableFormats.Flag },
        new() { Key = "model", HeaderKey = "Table_Model", Width = 0 },
        new() { Key = "serial", HeaderKey = "Table_Serial", Width = 150 },
        new() { Key = "health", HeaderKey = "Table_Health", Width = 80 },
        new() { Key = "wearout", HeaderKey = "Table_Wearout", Width = 70 }
    ];

    private static readonly IReadOnlyList<TableColumn> VolumeGroupColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 0 },
        new() { Key = "size", HeaderKey = "Table_Size", Width = 110, Format = TableFormats.Bytes },
        new() { Key = "free", HeaderKey = "NodeDisks_Free", Width = 110, Format = TableFormats.Bytes }
    ];

    private static readonly IReadOnlyList<TableColumn> ThinPoolColumns =
    [
        new() { Key = "lv", HeaderKey = "Table_Name", Width = 0 },
        new() { Key = "vg", HeaderKey = "NodeDisks_VolumeGroup", Width = 140 },
        new() { Key = "lv_size", HeaderKey = "Table_Size", Width = 110, Format = TableFormats.Bytes },
        new() { Key = "used", HeaderKey = "NodeDisks_Used", Width = 110, Format = TableFormats.Bytes },
        new() { Key = "metadata_size", HeaderKey = "NodeDisks_Metadata", Width = 110, Format = TableFormats.Bytes }
    ];

    private static readonly IReadOnlyList<TableColumn> DirectoryColumns =
    [
        new() { Key = "path", HeaderKey = "Table_Path", Width = 0 },
        new() { Key = "device", HeaderKey = "Table_Device", Width = 160 },
        new() { Key = "type", HeaderKey = "NodeDisks_FileSystem", Width = 90 },
        new() { Key = "options", HeaderKey = "NodeDisks_MountOptions", Width = 160 }
    ];

    private static readonly IReadOnlyList<TableColumn> ZfsColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 0 },
        new() { Key = "size", HeaderKey = "Table_Size", Width = 100, Format = TableFormats.Bytes },
        new() { Key = "free", HeaderKey = "NodeDisks_Free", Width = 100, Format = TableFormats.Bytes },
        new() { Key = "alloc", HeaderKey = "NodeDisks_Used", Width = 100, Format = TableFormats.Bytes },
        new() { Key = "frag", HeaderKey = "NodeDisks_Fragmentation", Width = 70 },
        new() { Key = "dedup", HeaderKey = "NodeDisks_Dedup", Width = 70 },
        new() { Key = "health", HeaderKey = "Table_Health", Width = 90 }
    ];

    private static readonly IReadOnlyList<TableColumn> SmartColumns =
    [
        new() { Key = "id", HeaderKey = "Table_Id", Width = 50 },
        new() { Key = "name", HeaderKey = "Table_Name", Width = 0 },
        new() { Key = "value", HeaderKey = "NodeDisks_SmartValue", Width = 70 },
        new() { Key = "worst", HeaderKey = "NodeDisks_SmartWorst", Width = 70 },
        new() { Key = "threshold", HeaderKey = "NodeDisks_SmartThreshold", Width = 70 },
        new() { Key = "raw", HeaderKey = "NodeDisks_SmartRaw", Width = 140 },
        new() { Key = "fail", HeaderKey = "NodeDisks_SmartFail", Width = 80 }
    ];

    private static readonly IReadOnlyList<(string, string)> RaidLevels =
    [
        ("single", "NodeDisks_RaidSingle"), ("mirror", "NodeDisks_RaidMirror"), ("raid10", "RAID10"),
        ("raidz", "RAIDZ"), ("raidz2", "RAIDZ2"), ("raidz3", "RAIDZ3")
    ];

    private static readonly IReadOnlyList<(string, string)> Compressions =
        [("on", "on"), ("lz4", "lz4"), ("zstd", "zstd"), ("gzip", "gzip"), ("off", "off")];

    public static SubTabsView Create(ProxmoxApiClient api, string node, bool canEdit)
    {
        return new SubTabsView(
        [
            ("NodeDisks_TabDisks", () => new TableTab(() => api.Disks.ListAsync(node), DiskColumns,
                "NodeDisks_Hint", DiskActions(api, node, canEdit))),
            ("NodeDisks_TabLvm", () => new TableTab(() => api.Disks.VolumeGroupsAsync(node),
                VolumeGroupColumns, "NodeDisks_LvmHint",
                canEdit ? StorageActions(api, node, "lvm", "name") : null)),
            ("NodeDisks_TabLvmThin", () => new TableTab(() => api.Disks.ListStorageAsync(node, "lvmthin"),
                ThinPoolColumns, "NodeDisks_LvmThinHint",
                canEdit ? StorageActions(api, node, "lvmthin", "lv") : null)),
            ("NodeDisks_TabDirectory", () => new TableTab(() => api.Disks.ListStorageAsync(node, "directory"),
                DirectoryColumns, "NodeDisks_DirectoryHint",
                canEdit ? StorageActions(api, node, "directory", "path") : null)),
            ("NodeDisks_TabZfs", () => new TableTab(() => api.Disks.ListStorageAsync(node, "zfs"), ZfsColumns,
                "NodeDisks_ZfsHint", canEdit ? StorageActions(api, node, "zfs", "name") : null))
        ]);
    }

    private static IReadOnlyList<TableAction> DiskActions(ProxmoxApiClient api, string node, bool canEdit)
    {
        var smart = new TableAction
        {
            LabelKey = "NodeDisks_Smart", IconKey = "IconList", NeedsSelection = true,
            Run = (row, owner) => ShowSmartAsync(api, node, row!["devpath"], owner)
        };
        if (!canEdit) return [smart];

        return
        [
            smart,
            new TableAction
            {
                LabelKey = "NodeDisks_InitGpt", IconKey = "IconPlus", NeedsSelection = true,
                Confirm = row => Loc.T("NodeDisks_InitGptConfirm", row!["devpath"]),
                Run = async (row, _) => await RunTaskAsync(api,
                    api.Disks.InitGptAsync(node, row!["devpath"]), "NodeDisks_InitGptDone")
            },
            new TableAction
            {
                LabelKey = "NodeDisks_Wipe", IconKey = "IconTrash", NeedsSelection = true,
                Run = (row, owner) =>
                {
                    var device = row!["devpath"];
                    return SubmitTaskAsync(api, owner, Loc.T("NodeDisks_WipeTitle", device),
                        [TypeToConfirmField()],
                        _ => api.Disks.WipeAsync(node, device),
                        "NodeDisks_WipeDone", TypedMatches(device));
                }
            }
        ];
    }

    private static async Task<string?> ShowSmartAsync(ProxmoxApiClient api, string node, string device,
        Window? owner)
    {
        // ATA 는 속성표, NVMe 등은 smartctl 원문만 준다 — 원문은 칸이 맞지 않으므로 고정폭 글꼴 그대로 보여 준다
        var attributes = await api.Disks.SmartAttributesAsync(node, device);
        UIElement content = attributes.Count > 0
            ? new TableTab(() => api.Disks.SmartAttributesAsync(node, device), SmartColumns, "NodeDisks_SmartHint")
            : new TextEditTab(async () => (await api.Disks.SmartTextAsync(node, device), string.Empty), null,
                "NodeDisks_SmartHint");
        return TableWindow.ShowModal(owner, Loc.T("NodeDisks_SmartTitle", device), content);
    }

    /// <summary>
    ///     LVM·LVM-Thin·디렉터리·ZFS 공통 — 만들기와 없애기. nameKey 는 행에서 이름을 꺼낼 필드
    ///     (디렉터리는 경로의 마지막 부분이 이름).
    /// </summary>
    private static IReadOnlyList<TableAction> StorageActions(ProxmoxApiClient api, string node, string kind,
        string nameKey)
    {
        return
        [
            new TableAction
            {
                LabelKey = "NodeDisks_Create", IconKey = "IconPlus",
                Run = (_, owner) => CreateAsync(api, node, kind, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Requires = api.Disks.Feature(nameof(DisksApi.DeleteStorageAsync)),
                Run = (row, owner) =>
                {
                    var name = Value(row!, nameKey);
                    if (kind == "directory") name = name.TrimEnd('/').Split('/')[^1];

                    return SubmitTaskAsync(api, owner, Loc.T("NodeDisks_DeleteTitle", name),
                    [
                        new FormField
                        {
                            Key = "cleanup-config", LabelKey = "NodeDisks_CleanupConfig", Kind = FormFieldKind.Bool,
                            Initial = "1"
                        },
                        new FormField
                        {
                            Key = "cleanup-disks", LabelKey = "NodeDisks_CleanupDisks", Kind = FormFieldKind.Bool
                        },
                        TypeToConfirmField()
                    ], values => api.Disks.DeleteStorageAsync(node, kind, name, values["cleanup-config"] == "1",
                        values["cleanup-disks"] == "1", kind == "lvmthin" ? Value(row!, "vg") : null),
                        "NodeDisks_Deleted", TypedMatches(name));
                }
            }
        ];
    }

    private static async Task<string?> CreateAsync(ProxmoxApiClient api, string node, string kind,
        Window? owner)
    {
        var unused = (await api.Disks.ListAsync(node, unusedOnly: true))
            .Select(d => (Value(d, "devpath"), $"{Value(d, "devpath")}  {Value(d, "model")}".Trim()))
            .ToList();

        var fields = new List<FormField> { new() { Key = "name", LabelKey = "Table_Name", Required = true } };
        if (kind == "zfs")
        {
            fields.Add(new FormField
            {
                Key = "devices", LabelKey = "NodeDisks_Devices", Kind = FormFieldKind.MultiChoice, Choices = unused,
                Required = true
            });
            fields.Add(new FormField
            {
                Key = "raidlevel", LabelKey = "NodeDisks_RaidLevel", Kind = FormFieldKind.Choice, Choices = RaidLevels,
                Initial = "single"
            });
            fields.Add(new FormField
            {
                Key = "compression", LabelKey = "NodeDisks_Compression", Kind = FormFieldKind.Choice,
                Choices = Compressions, Initial = "on"
            });
            fields.Add(new FormField { Key = "ashift", LabelKey = "NodeDisks_Ashift", Initial = "12" });
        }
        else
        {
            fields.Add(new FormField
            {
                Key = "device", LabelKey = "Table_Device", Kind = FormFieldKind.Choice, Choices = unused,
                Required = true
            });
            if (kind == "directory")
                fields.Add(new FormField
                {
                    Key = "filesystem", LabelKey = "NodeDisks_FileSystem", Kind = FormFieldKind.Choice,
                    Choices = [("ext4", "ext4"), ("xfs", "xfs")], Initial = "ext4"
                });
        }

        fields.Add(new FormField
        {
            Key = "add_storage", LabelKey = "NodeDisks_AddStorage", Kind = FormFieldKind.Bool, Initial = "1"
        });

        return await SubmitTaskAsync(api, owner, Loc.T("NodeDisks_CreateTitle", Loc.T(KindLabelKey(kind))), fields,
            values => api.Disks.CreateStorageAsync(node, kind, NonEmpty(values)), "NodeDisks_Created");
    }

    private static string KindLabelKey(string kind)
    {
        return kind switch
        {
            "lvm" => "NodeDisks_TabLvm",
            "lvmthin" => "NodeDisks_TabLvmThin",
            "directory" => "NodeDisks_TabDirectory",
            _ => "NodeDisks_TabZfs"
        };
    }
}

using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
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
        var basePath = $"nodes/{Seg(node)}/disks";
        return new SubTabsView(
        [
            ("NodeDisks_TabDisks", () => new TableTab(() => api.GetTableAsync($"{basePath}/list"), DiskColumns,
                "NodeDisks_Hint", DiskActions(api, basePath, canEdit))),
            ("NodeDisks_TabLvm", () => new TableTab(() => api.GetArrayPropertyAsync($"{basePath}/lvm", "children"),
                VolumeGroupColumns, "NodeDisks_LvmHint",
                canEdit ? StorageActions(api, basePath, "lvm", "name") : null)),
            ("NodeDisks_TabLvmThin", () => new TableTab(() => api.GetTableAsync($"{basePath}/lvmthin"),
                ThinPoolColumns, "NodeDisks_LvmThinHint",
                canEdit ? StorageActions(api, basePath, "lvmthin", "lv") : null)),
            ("NodeDisks_TabDirectory", () => new TableTab(() => api.GetTableAsync($"{basePath}/directory"),
                DirectoryColumns, "NodeDisks_DirectoryHint",
                canEdit ? StorageActions(api, basePath, "directory", "path") : null)),
            ("NodeDisks_TabZfs", () => new TableTab(() => api.GetTableAsync($"{basePath}/zfs"), ZfsColumns,
                "NodeDisks_ZfsHint", canEdit ? StorageActions(api, basePath, "zfs", "name") : null))
        ]);
    }

    private static IReadOnlyList<TableAction> DiskActions(ProxmoxApiClient api, string basePath, bool canEdit)
    {
        var smart = new TableAction
        {
            LabelKey = "NodeDisks_Smart", IconKey = "IconList", NeedsSelection = true,
            Run = (row, owner) => ShowSmartAsync(api, basePath, row!["devpath"], owner)
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
                    api.PostActionAsync($"{basePath}/initgpt",
                        new Dictionary<string, string> { ["disk"] = row!["devpath"] }), "NodeDisks_InitGptDone")
            },
            new TableAction
            {
                LabelKey = "NodeDisks_Wipe", IconKey = "IconTrash", NeedsSelection = true,
                Run = (row, owner) =>
                {
                    var device = row!["devpath"];
                    return SubmitTaskAsync(api, owner, Loc.T("NodeDisks_WipeTitle", device),
                        [TypeToConfirmField()],
                        _ => api.PutActionAsync($"{basePath}/wipedisk",
                            new Dictionary<string, string> { ["disk"] = device }),
                        "NodeDisks_WipeDone", TypedMatches(device));
                }
            }
        ];
    }

    private static Task<string?> ShowSmartAsync(ProxmoxApiClient api, string basePath, string device, Window? owner)
    {
        var path = $"{basePath}/smart?disk={Uri.EscapeDataString(device)}";
        return Task.FromResult(TableWindow.ShowModal(owner, Loc.T("NodeDisks_SmartTitle", device),
            new TableTab(async () =>
            {
                var attributes = await api.GetArrayPropertyAsync(path, "attributes");
                if (attributes.Count > 0) return attributes;

                // NVMe 등은 속성표 대신 원문 텍스트를 준다 — 줄마다 한 행으로 보여 준다
                var text = Value(await api.GetObjectAsync(path), "text");
                return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                    {
                        ["name"] = line.TrimEnd()
                    })
                    .ToList();
            }, SmartColumns, "NodeDisks_SmartHint")));
    }

    /// <summary>
    ///     LVM·LVM-Thin·디렉터리·ZFS 공통 — 만들기와 없애기. nameKey 는 행에서 이름을 꺼낼 필드
    ///     (디렉터리는 경로의 마지막 부분이 이름).
    /// </summary>
    private static IReadOnlyList<TableAction> StorageActions(ProxmoxApiClient api, string basePath, string kind,
        string nameKey)
    {
        return
        [
            new TableAction
            {
                LabelKey = "NodeDisks_Create", IconKey = "IconPlus",
                Run = (_, owner) => CreateAsync(api, basePath, kind, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
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
                    ], values =>
                    {
                        var query = $"cleanup-config={values["cleanup-config"]}"
                                    + $"&cleanup-disks={values["cleanup-disks"]}";
                        if (kind == "lvmthin")
                            query += $"&volume-group={Uri.EscapeDataString(Value(row!, "vg"))}";
                        return api.DeleteActionAsync($"{basePath}/{kind}/{Seg(name)}?{query}");
                    }, "NodeDisks_Deleted", TypedMatches(name));
                }
            }
        ];
    }

    private static async Task<string?> CreateAsync(ProxmoxApiClient api, string basePath, string kind,
        Window? owner)
    {
        var unused = (await api.GetTableAsync($"{basePath}/list?type=unused"))
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
            values => api.PostActionAsync($"{basePath}/{kind}", NonEmpty(values)), "NodeDisks_Created");
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

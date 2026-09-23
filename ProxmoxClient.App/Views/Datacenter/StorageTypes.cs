using ProxmoxClient.App.Views.Shared;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>추가할 수 있는 저장소 유형 — 유형마다 필요한 입력과 기본 콘텐츠.</summary>
internal sealed record StorageType(string Type, string LabelKey, string DefaultContent, IReadOnlyList<FormField> Fields)
{
    private const string FileContent = "images,rootdir,iso,vztmpl,backup";
    private const string BlockContent = "images,rootdir";

    public static IReadOnlyList<StorageType> All { get; } =
    [
        new("dir", "StorageType_Dir", FileContent,
        [
            new FormField { Key = "path", LabelKey = "Table_Path", Required = true }
        ]),
        new("nfs", "StorageType_Nfs", FileContent,
        [
            new FormField { Key = "server", LabelKey = "DcStorage_Server", Required = true },
            new FormField { Key = "export", LabelKey = "DcStorage_Export", Required = true }
        ]),
        new("cifs", "StorageType_Cifs", FileContent,
        [
            new FormField { Key = "server", LabelKey = "DcStorage_Server", Required = true },
            new FormField { Key = "share", LabelKey = "DcStorage_Share", Required = true },
            new FormField { Key = "username", LabelKey = "DcUsers_UserName" },
            new FormField { Key = "password", LabelKey = "DcStorage_Password", Kind = FormFieldKind.Password },
            new FormField { Key = "domain", LabelKey = "DcStorage_Domain" },
            new FormField { Key = "subdir", LabelKey = "DcStorage_Subdir" }
        ]),
        new("pbs", "StorageType_Pbs", "backup",
        [
            new FormField { Key = "server", LabelKey = "DcStorage_Server", Required = true },
            new FormField { Key = "datastore", LabelKey = "DcStorage_Datastore", Required = true },
            new FormField { Key = "username", LabelKey = "DcUsers_UserName", Required = true },
            new FormField { Key = "password", LabelKey = "DcStorage_Password", Kind = FormFieldKind.Password },
            new FormField { Key = "fingerprint", LabelKey = "Table_Fingerprint" },
            new FormField { Key = "namespace", LabelKey = "DcStorage_Namespace" }
        ]),
        new("zfspool", "StorageType_ZfsPool", BlockContent,
        [
            new FormField { Key = "pool", LabelKey = "DcStorage_Pool", Required = true }
        ]),
        new("lvmthin", "StorageType_LvmThin", BlockContent,
        [
            new FormField { Key = "vgname", LabelKey = "DcStorage_VolumeGroup", Required = true },
            new FormField { Key = "thinpool", LabelKey = "DcStorage_ThinPool", Required = true }
        ])
    ];
}

using ProxmoxClient.App.Views.Shared;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>저장소 한 칸 — 만든 뒤에는 바꿀 수 없는 칸(경로·서버·풀 등)은 수정 창에서 보여 주기만 한다.</summary>
internal sealed record StorageField(FormField Field, bool CreateOnly = false);

/// <summary>
///     저장소 유형(웹 UI storage/*Edit.js) — 유형별 칸, 고를 수 있는 콘텐츠와 기본값, 공유·보존·사전 할당 여부.
///     <see cref="FixedContent" /> 가 있으면 콘텐츠는 고정(PBS=backup, ESXi=import).
/// </summary>
internal sealed record StorageType(
    string Type,
    string LabelKey,
    IReadOnlyList<string> Contents,
    string DefaultContent,
    IReadOnlyList<StorageField> Fields)
{
    private static readonly string[] FileContents =
        ["images", "rootdir", "vztmpl", "iso", "backup", "snippets", "import"];
    private static readonly string[] BlockContents = ["images", "rootdir"];

    public string? FixedContent { get; init; }

    /// <summary>공유 저장소 표시 칸(디렉터리·LVM — 다른 유형은 원래 공유이거나 로컬이다).</summary>
    public bool HasShared { get; init; }

    /// <summary>사전 할당(디렉터리·Btrfs·NFS·CIFS).</summary>
    public bool HasPreallocation { get; init; }

    /// <summary>백업을 담을 수 있으면 보존(prune-backups) 칸을 둔다.</summary>
    public bool HasRetention => FixedContent == "backup" || Contents.Contains("backup");

    private static StorageField F(string key, string labelKey, bool required = false, bool createOnly = false,
        string? hint = null, bool advanced = false)
    {
        return new StorageField(new FormField
        {
            Key = key, LabelKey = labelKey, Required = required, Trim = true, Hint = hint, Advanced = advanced
        }, createOnly);
    }

    private static StorageField Secret(string key, string labelKey, bool requiredOnCreate = false)
    {
        return new StorageField(new FormField
        {
            Key = key, LabelKey = labelKey, Kind = FormFieldKind.Password, Required = requiredOnCreate
        });
    }

    private static StorageField Bool(string key, string labelKey, string initial = "0", bool advanced = false)
    {
        return new StorageField(new FormField
        {
            Key = key, LabelKey = labelKey, Kind = FormFieldKind.Bool, Initial = initial, Advanced = advanced
        });
    }

    private static StorageField Choice(string key, string labelKey, (string, string)[] choices, bool advanced = true,
        bool createOnly = false)
    {
        return new StorageField(new FormField
        {
            Key = key, LabelKey = labelKey, Kind = FormFieldKind.Choice, Choices = choices, Advanced = advanced
        }, createOnly);
    }

    public static IReadOnlyList<StorageType> All { get; } =
    [
        new("dir", "StorageType_Dir", FileContents, "images",
            [F("path", "Table_Path", true, true, "StorageHint_Path")]) { HasShared = true, HasPreallocation = true },
        new("lvm", "StorageType_Lvm", BlockContents, "images,rootdir",
        [
            F("vgname", "DcStorage_VolumeGroup", true, true),
            F("base", "StorageField_Base", createOnly: true, hint: "StorageHint_Base", advanced: true),
            Bool("saferemove", "StorageField_SafeRemove", advanced: true)
        ]) { HasShared = true },
        new("lvmthin", "StorageType_LvmThin", BlockContents, "images,rootdir",
            [F("vgname", "DcStorage_VolumeGroup", true, true), F("thinpool", "DcStorage_ThinPool", true, true)]),
        new("btrfs", "StorageType_Btrfs", FileContents, "images,rootdir",
            [F("path", "Table_Path", true, true, "StorageHint_Path")]) { HasPreallocation = true },
        new("nfs", "StorageType_Nfs", FileContents, "images",
        [
            F("server", "DcStorage_Server", true, true), F("export", "DcStorage_Export", true, true),
            Choice("options", "StorageField_NfsVersion",
                [("", "StorageField_Default"), ("vers=3", "3"), ("vers=4", "4"), ("vers=4.1", "4.1"),
                    ("vers=4.2", "4.2")])
        ]) { HasPreallocation = true },
        new("cifs", "StorageType_Cifs", FileContents, "images",
        [
            F("server", "DcStorage_Server", true, true), F("share", "DcStorage_Share", true, true),
            F("username", "DcUsers_UserName"), Secret("password", "DcStorage_Password"),
            F("domain", "DcStorage_Domain"), F("subdir", "DcStorage_Subdir", createOnly: true),
            Choice("smbversion", "StorageField_SmbVersion",
                [("", "StorageField_Default"), ("2.0", "2.0"), ("2.1", "2.1"), ("3", "3"), ("3.0", "3.0"),
                    ("3.11", "3.11")])
        ]) { HasPreallocation = true },
        new("iscsi", "StorageType_Iscsi", ["images", "none"], "images",
            [F("portal", "StorageField_Portal", true, true), F("target", "StorageField_Target", true, true)]),
        new("cephfs", "StorageType_CephFs", ["vztmpl", "iso", "backup", "snippets", "import"], "backup",
        [
            F("monhost", "StorageField_Monhost", createOnly: true, hint: "StorageHint_Monhost"),
            F("username", "DcUsers_UserName", hint: "StorageHint_CephUser"),
            F("fs-name", "StorageField_FsName", createOnly: true),
            new StorageField(new FormField { Key = "keyring", LabelKey = "StorageField_Secret",
                Kind = FormFieldKind.Multiline, Hint = "StorageHint_External" }, true)
        ]),
        new("rbd", "StorageType_Rbd", BlockContents, "images",
        [
            F("pool", "DcStorage_Pool", true, true),
            F("monhost", "StorageField_Monhost", createOnly: true, hint: "StorageHint_Monhost"),
            F("username", "DcUsers_UserName", hint: "StorageHint_CephUser"),
            new StorageField(new FormField { Key = "keyring", LabelKey = "StorageField_Keyring",
                Kind = FormFieldKind.Multiline, Hint = "StorageHint_External" }, true),
            Bool("krbd", "StorageField_Krbd"),
            F("namespace", "DcStorage_Namespace", createOnly: true, advanced: true),
            F("data-pool", "StorageField_DataPool", createOnly: true, advanced: true)
        ]),
        new("zfs", "StorageType_ZfsIscsi", ["images"], "images",
        [
            F("portal", "StorageField_Portal", true, true), F("pool", "DcStorage_Pool", true, true),
            F("blocksize", "StorageField_BlockSize", hint: "StorageHint_BlockSize"),
            F("target", "StorageField_Target", true, true),
            Choice("iscsiprovider", "StorageField_IscsiProvider",
                [("LIO", "LIO"), ("comstar", "Comstar"), ("istgt", "istgt"), ("iet", "IET")], false, true),
            Bool("sparse", "StorageField_Sparse"), Bool("nowritecache", "StorageField_WriteCacheOff", "1"),
            F("lio_tpg", "StorageField_LioTpg", hint: "StorageHint_LioTpg"),
            F("comstar_hg", "StorageField_ComstarHg", advanced: true),
            F("comstar_tg", "StorageField_ComstarTg", advanced: true)
        ]),
        new("zfspool", "StorageType_ZfsPool", BlockContents, "images,rootdir",
        [
            F("pool", "DcStorage_Pool", true, true), Bool("sparse", "StorageField_Sparse"),
            F("blocksize", "StorageField_BlockSize", hint: "StorageHint_BlockSize")
        ]),
        new("pbs", "StorageType_Pbs", ["backup"], "backup",
        [
            F("server", "DcStorage_Server", true), F("port", "StorageField_Port", hint: "StorageHint_PbsPort",
                advanced: true),
            F("username", "DcUsers_UserName", true, hint: "StorageHint_PbsUser"),
            Secret("password", "DcStorage_Password", true),
            F("datastore", "DcStorage_Datastore", true, true), F("namespace", "DcStorage_Namespace"),
            F("fingerprint", "Table_Fingerprint", hint: "StorageHint_Fingerprint")
        ]) { FixedContent = "backup" },
        new("esxi", "StorageType_Esxi", ["import"], "import",
        [
            F("server", "DcStorage_Server", true, true), F("username", "DcUsers_UserName", true, true),
            Secret("password", "DcStorage_Password", true),
            Bool("skip-cert-verification", "StorageField_SkipCert")
        ]) { FixedContent = "import" }
    ];
}

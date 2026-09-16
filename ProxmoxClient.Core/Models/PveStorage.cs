using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Models;

/// <summary>
///     클러스터 스토리지 항목 (GET /cluster/resources?type=storage).
///     행 객체 유지 갱신을 위해 <see cref="CopyFrom" /> 제공 — 값이 바뀐 속성(과 파생 속성)만 알린다.
/// </summary>
public sealed class PveStorage : ObservableModel
{
    /// <summary>리소스 ID, 예: "storage/pve/local-lvm".</summary>
    public string Id
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    /// <summary>스토리지 이름, 예: "local-lvm".</summary>
    public string Storage
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    public string Node
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    /// <summary>백엔드 유형(lvmthin, dir, nfs, cephfs, zfs ...).</summary>
    public string PluginType
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    /// <summary>저장 콘텐츠(images, rootdir, backup, iso ...).</summary>
    public string Content
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    public long DiskBytes
    {
        get;
        set
        {
            if (SetField(ref field, value)) Raise(nameof(UsagePercent));
        }
    }

    public long MaxDiskBytes
    {
        get;
        set
        {
            if (SetField(ref field, value)) Raise(nameof(UsagePercent));
        }
    }

    public string Status
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                Raise(nameof(IsActive));
                Raise(nameof(StatusDisplay));
            }
        }
    } = string.Empty;

    public bool IsActive =>
        Status.Length == 0 || string.Equals(Status, "available", StringComparison.OrdinalIgnoreCase);

    /// <summary>상태 표시용 텍스트 — 빈 상태는 사용 가능로 표기.</summary>
    public string StatusDisplay =>
        IsActive ? Res.T("PveStorage_01") : Status.Length > 0 ? Status : Res.T("PveStorage_02");

    public double UsagePercent => MaxDiskBytes > 0 ? 100.0 * DiskBytes / MaxDiskBytes : 0;

    /// <summary>Copies values from a fresh snapshot; each setter notifies only when its value changed.</summary>
    public void CopyFrom(PveStorage other)
    {
        Id = other.Id;
        Storage = other.Storage;
        Node = other.Node;
        PluginType = other.PluginType;
        Content = other.Content;
        DiskBytes = other.DiskBytes;
        MaxDiskBytes = other.MaxDiskBytes;
        Status = other.Status;
    }
}
namespace ProxmoxClient.Core.Models;

/// <summary>
///     A guest (QEMU VM or LXC container) as reported by the cluster resource index
///     or a per-node guest list. Byte sizes and fractions are already normalized.
///     Rows are kept alive across refreshes via <see cref="CopyFrom" />; only changed properties are notified.
/// </summary>
public sealed class PveResource : ObservableModel
{
    /// <summary>업타임 값을 받은 시각 — 그 뒤 경과 시간을 더해 표시한다.</summary>
    private long _uptimeSampledAtTick = Environment.TickCount64;

    /// <summary>Cluster resource id, e.g. "qemu/100".</summary>
    public string Id
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    /// <summary>Guest kind (VM or CT).</summary>
    public ResourceKind Kind
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Guest numeric id, e.g. 100.</summary>
    public int VmId
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Display name; falls back to "VM-100"/"CT-100" when the guest is unnamed.</summary>
    public string Name
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    /// <summary>Node the guest currently runs on.</summary>
    public string Node
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    /// <summary>Raw Proxmox status string ("running", "stopped", "paused", ...).</summary>
    public string Status
    {
        get;
        set
        {
            if (SetField(ref field, value)) Raise(nameof(IsRunning));
        }
    } = string.Empty;

    /// <summary>CPU usage in percent (0-100) relative to all assigned cores.</summary>
    public double CpuUsagePercent
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Number of assigned CPU cores.</summary>
    public int CpuCount
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Used memory in bytes.</summary>
    public long MemBytes
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Maximum memory in bytes.</summary>
    public long MaxMemBytes
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Used disk in bytes (rootfs for CTs, first disk for VMs).</summary>
    public long DiskBytes
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Maximum disk in bytes.</summary>
    public long MaxDiskBytes
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Cumulative inbound network traffic in bytes (cluster index only).</summary>
    public long NetInBytes
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Cumulative outbound network traffic in bytes (cluster index only).</summary>
    public long NetOutBytes
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Uptime in seconds as last reported by the server (0 when stopped).</summary>
    public long UptimeSeconds
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                _uptimeSampledAtTick = Environment.TickCount64;
                Raise(nameof(UptimeDisplaySeconds));
            }
        }
    }

    /// <summary>
    ///     표시용 업타임 — 마지막 수신값 + 그 뒤 경과 시간.
    ///     /cluster/resources 값은 pvestatd 주기(수 초)로만 갱신돼 그대로 쓰면 화면이 멈춘 것처럼 보인다.
    /// </summary>
    public long UptimeDisplaySeconds =>
        UptimeSeconds <= 0 ? 0 : UptimeSeconds + (Environment.TickCount64 - _uptimeSampledAtTick) / 1000;

    /// <summary>True when the guest is a template (power operations are not allowed).</summary>
    public bool IsTemplate
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>관리자가 지정한 태그 목록(콤마 구분 문자열에서 파싱). 내용이 같으면 새 배열이어도 알리지 않는다(태그 칩 재생성 방지).</summary>
    public string[] Tags
    {
        get;
        set
        {
            value ??= [];
            if (field.AsSpan().SequenceEqual(value)) return;

            field = value;
            Raise(nameof(Tags));
        }
    } = [];

    /// <summary>소속 풀 이름(있으면).</summary>
    public string? Pool
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Convenience flag: raw status equals "running".</summary>
    public bool IsRunning => string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase);

    /// <summary>1초 주기 화면 갱신 — 서버 값이 그대로여도 경과 시간을 다시 계산하게 알린다.</summary>
    public void TickUptime()
    {
        if (UptimeSeconds > 0) Raise(nameof(UptimeDisplaySeconds));
    }

    /// <summary>상태만 갱신(전원 작업 직후 실시간 조회 결과 반영). 바뀐 경우에만 바인딩에 알린다.</summary>
    public void UpdateStatus(string status)
    {
        Status = status;
    }

    /// <summary>Copies values from a fresh snapshot; each setter notifies only when its value changed.</summary>
    public void CopyFrom(PveResource other)
    {
        Id = other.Id;
        Kind = other.Kind;
        VmId = other.VmId;
        Name = other.Name;
        Node = other.Node;
        Status = other.Status;
        CpuUsagePercent = other.CpuUsagePercent;
        CpuCount = other.CpuCount;
        MemBytes = other.MemBytes;
        MaxMemBytes = other.MaxMemBytes;
        DiskBytes = other.DiskBytes;
        MaxDiskBytes = other.MaxDiskBytes;
        NetInBytes = other.NetInBytes;
        NetOutBytes = other.NetOutBytes;
        UptimeSeconds = other.UptimeSeconds;
        IsTemplate = other.IsTemplate;
        Tags = other.Tags;
        Pool = other.Pool;
    }
}
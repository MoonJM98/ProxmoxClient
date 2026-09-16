namespace ProxmoxClient.Core.Models;

/// <summary>
///     A physical cluster node as listed by GET /nodes.
///     Rows are kept alive across refreshes: <see cref="CopyFrom" /> mutates in place
///     and notifies only the properties whose values changed, so list UI updates without row churn.
/// </summary>
public sealed class PveNode : ObservableModel
{
    /// <summary>업타임 값을 받은 시각 — 그 뒤 경과 시간을 더해 표시한다.</summary>
    private long _uptimeSampledAtTick = Environment.TickCount64;

    /// <summary>Node name.</summary>
    public string Node
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    /// <summary>Raw status string ("online", "offline", "unknown").</summary>
    public string Status
    {
        get;
        set
        {
            if (SetField(ref field, value)) Raise(nameof(IsOnline));
        }
    } = string.Empty;

    /// <summary>CPU usage in percent (0-100) across all cores.</summary>
    public double CpuUsagePercent
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Total logical CPU count.</summary>
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

    /// <summary>Total memory in bytes.</summary>
    public long MaxMemBytes
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Used root disk in bytes.</summary>
    public long DiskBytes
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Total root disk in bytes.</summary>
    public long MaxDiskBytes
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Uptime in seconds as last reported by the server (0 when offline).</summary>
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

    /// <summary>표시용 업타임 — 마지막 수신값 + 그 뒤 경과 시간(서버 값은 수 초 주기로만 갱신된다).</summary>
    public long UptimeDisplaySeconds =>
        UptimeSeconds <= 0 ? 0 : UptimeSeconds + (Environment.TickCount64 - _uptimeSampledAtTick) / 1000;

    /// <summary>Subscription level, when reported.</summary>
    public string? Level
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Convenience flag: raw status equals "online".</summary>
    public bool IsOnline => string.Equals(Status, "online", StringComparison.OrdinalIgnoreCase);

    /// <summary>1초 주기 화면 갱신 — 서버 값이 그대로여도 경과 시간을 다시 계산하게 알린다.</summary>
    public void TickUptime()
    {
        if (UptimeSeconds > 0) Raise(nameof(UptimeDisplaySeconds));
    }

    /// <summary>Copies values from a fresh snapshot; each setter notifies only when its value changed.</summary>
    public void CopyFrom(PveNode other)
    {
        Node = other.Node;
        Status = other.Status;
        CpuUsagePercent = other.CpuUsagePercent;
        CpuCount = other.CpuCount;
        MemBytes = other.MemBytes;
        MaxMemBytes = other.MaxMemBytes;
        DiskBytes = other.DiskBytes;
        MaxDiskBytes = other.MaxDiskBytes;
        UptimeSeconds = other.UptimeSeconds;
        Level = other.Level;
    }
}
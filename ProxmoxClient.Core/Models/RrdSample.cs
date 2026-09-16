namespace ProxmoxClient.Core.Models;

/// <summary>GET /nodes/{n}/{kind}/{vmid}/rrddata 표본 하나 (결측 필드는 null).</summary>
public sealed class RrdSample
{
    public long TimeUnix { get; init; }

    /// <summary>CPU 사용률(0..1).</summary>
    public double? Cpu { get; init; }

    /// <summary>사용 메모리(바이트).</summary>
    public double? Mem { get; init; }

    /// <summary>최대 메모리(바이트).</summary>
    public double? MaxMem { get; init; }

    /// <summary>수신 속도(바이트/초 평균).</summary>
    public double? NetIn { get; init; }

    /// <summary>송신 속도(바이트/초 평균).</summary>
    public double? NetOut { get; init; }

    /// <summary>디스크 읽기 속도(바이트/초 평균, 게스트 전용).</summary>
    public double? DiskRead { get; init; }

    /// <summary>디스크 쓰기 속도(바이트/초 평균, 게스트 전용).</summary>
    public double? DiskWrite { get; init; }
}
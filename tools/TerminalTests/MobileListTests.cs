using ProxmoxClient.UI.Views.Shared;

/// <summary>휴대폰 목록 카드 — 상태 칩 색 가르기, 볼륨 ID 제목 줄이기.</summary>
public static class MobileListTests
{
    public static void StatusTones(Checker c)
    {
        c.Equal(StatusChip.Tone.Ok, StatusChip.Classify("OK"), "작업 OK");
        c.Equal(StatusChip.Tone.Ok, StatusChip.Classify("active"), "서비스 active");
        c.Equal(StatusChip.Tone.Error, StatusChip.Classify("ERROR: VM quit/powerdown failed"), "작업 오류");
        c.Equal(StatusChip.Tone.Error, StatusChip.Classify("failed"), "서비스 failed");
        c.Equal(StatusChip.Tone.Warn, StatusChip.Classify("HEALTH_WARN"), "Ceph 경고");
        c.Equal(StatusChip.Tone.Busy, StatusChip.Classify("running"), "실행 중 작업");
        c.Equal(StatusChip.Tone.Neutral, StatusChip.Classify("stopped"), "멈춤은 중립");
        c.Equal(StatusChip.Tone.Neutral, StatusChip.Classify(""), "빈 값");
    }

    public static void VolumeTitles(Checker c)
    {
        c.Equal("vzdump-qemu-100-2026_11_03-02_00_00.vma.zst",
            TableTab.ShortTitle("local:backup/vzdump-qemu-100-2026_11_03-02_00_00.vma.zst"), "백업 파일");
        c.Equal("debian-13.iso", TableTab.ShortTitle("nas:iso/debian-13.iso"), "ISO");
        c.Equal("local-lvm:vm-100-disk-0", TableTab.ShortTitle("local-lvm:vm-100-disk-0"), "디스크 볼륨(경로 없음)은 그대로");
        c.Equal("https://acme.example/dir", TableTab.ShortTitle("https://acme.example/dir"), "URL 은 그대로");
        c.Equal("fe80::1/64", TableTab.ShortTitle("fe80::1/64"), "IPv6 CIDR 은 그대로");
        c.Equal("/dev/sda", TableTab.ShortTitle("/dev/sda"), "장치 경로는 그대로");
    }
}

using System.Globalization;
using System.Net.Sockets;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Presentation.Guest.Tabs;

/// <summary>관리 기능의 순수 검사 — property string 규칙, 숫자 읽기, 입력 검사, digest·업로드 확장자.</summary>
public static class ManagementTests
{
    /// <summary>PVE property string 에는 따옴표가 없다 — 모든 쉼표에서 나누고, 쉼표·줄바꿈 든 값은 쓸 수 없다.</summary>
    public static void PropertyStringRules(Checker c)
    {
        var p = PropertyString.Parse("local-lvm:vm-100-disk-0,size=32G,mountoptions=noatime;nodev", "file");
        c.Equal("local-lvm:vm-100-disk-0", p.Get("file"), "기본 키(file)");
        c.Equal("noatime;nodev", p.Get("mountoptions"), "세미콜론 목록은 값 그대로");
        c.Equal("local-lvm:vm-100-disk-0,size=32G,mountoptions=noatime;nodev", p.Format("file"), "다시 조립하면 같은 모양");

        // '=' 는 첫 번째 것만 키를 나눈다(서버 정규식 ^([^=]+)=(.+)$ 와 같다)
        var eq = PropertyString.Parse("source=/dev/urandom,x=a=b");
        c.Equal("a=b", eq.Get("x"), "값 안의 = 는 그대로");

        // 따옴표는 특별한 뜻이 없다 — 서버처럼 쉼표에서 나눈다
        var quoted = PropertyString.Parse("mp=\"/a,b\",size=1G");
        c.Equal("\"/a", quoted.Get("mp"), "따옴표 안 쉼표도 나눈다(PVE 와 같음)");

        c.Is(PropertyString.IsValidValue("/mnt/data dir"), "공백은 값에 쓸 수 있다");
        c.Is(!PropertyString.IsValidValue("/mnt/a,b"), "쉼표는 쓸 수 없다");
        c.Is(!PropertyString.IsValidValue("a\nb"), "줄바꿈은 쓸 수 없다");
    }

    public static void MountPath(Checker c)
    {
        c.Is(GuestInputChecks.IsMountPath("/mnt/data"), "/mnt/data");
        c.Is(GuestInputChecks.IsMountPath("/srv/my data"), "공백 든 경로");
        c.Is(!GuestInputChecks.IsMountPath("/"), "루트는 안 된다");
        c.Is(!GuestInputChecks.IsMountPath("mnt/data"), "상대 경로");
        c.Is(!GuestInputChecks.IsMountPath("/mnt/a,b"), "쉼표");
        c.Is(!GuestInputChecks.IsMountPath("/mnt/../etc"), "/../");
        c.Is(!GuestInputChecks.IsMountPath("/mnt/./x"), "/./");
        c.Is(!GuestInputChecks.IsMountPath("/mnt/.."), "끝의 /..");
    }

    /// <summary>디스크 크기 — 소수점만, 지수·천 단위·부호는 받지 않고 문화권과 무관하다.</summary>
    public static void DiskSizeParsing(Checker c)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // 소수점이 쉼표인 문화권에서도
            c.Is(GuestInputChecks.TryParseGiB("1.5", out var v) && v == 1.5m, "1.5", v);
            c.Equal("1.5", v.ToString(CultureInfo.InvariantCulture), "서버로는 점으로");
            c.Is(GuestInputChecks.TryParseGiB("10", out var ten) && ten == 10m, "정수");
            c.Is(!GuestInputChecks.TryParseGiB("1e3", out _), "지수 표기 거부");
            c.Is(!GuestInputChecks.TryParseGiB("1,5", out _), "쉼표 거부");
            c.Is(!GuestInputChecks.TryParseGiB("-1", out _), "음수 거부");
            c.Is(!GuestInputChecks.TryParseGiB("0", out _), "0 거부");
            c.Is(!GuestInputChecks.TryParseGiB("", out _), "빈 값 거부");
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    public static void Validators(Checker c)
    {
        c.Is(GuestInputChecks.IsMacOrEmpty(""), "MAC 빈 값(자동)");
        c.Is(GuestInputChecks.IsMacOrEmpty("BC:24:11:AA:BB:CC"), "유니캐스트 MAC");
        c.Is(!GuestInputChecks.IsMacOrEmpty("01:00:5E:00:00:01"), "멀티캐스트 MAC 거부");
        c.Is(!GuestInputChecks.IsMacOrEmpty("BC:24:11:AA:BB"), "짧은 MAC 거부");
        c.Is(!GuestInputChecks.IsMacOrEmpty("BC-24-11-AA-BB-CC"), "- 구분 거부");

        c.Is(GuestInputChecks.IsSnapshotName("before-upgrade_2"), "스냅숏 이름");
        c.Is(!GuestInputChecks.IsSnapshotName("1st"), "숫자로 시작 거부");
        c.Is(!GuestInputChecks.IsSnapshotName("a"), "한 글자 거부");
        c.Is(!GuestInputChecks.IsSnapshotName("한글"), "한글 거부");
        c.Is(!GuestInputChecks.IsSnapshotName("a.b"), "점 거부");
        c.Is(GuestInputChecks.IsSnapshotName(new string('a', 40)), "40자");
        c.Is(!GuestInputChecks.IsSnapshotName(new string('a', 41)), "41자 거부");

        c.Is(GuestInputChecks.IsDescriptionLengthOk(new string('a', 8192)), "설명 8 KiB");
        c.Is(!GuestInputChecks.IsDescriptionLengthOk(new string('가', 2731)), "한글 2731자(8193바이트) 거부");

        c.Is(GuestInputChecks.IsBalloonOk("2048", "4096"), "벌룬 ≤ 메모리");
        c.Is(GuestInputChecks.IsBalloonOk("0", "512"), "벌룬 0(끄기)");
        c.Is(!GuestInputChecks.IsBalloonOk("8192", "4096"), "벌룬 > 메모리 거부");
        c.Is(GuestInputChecks.IsBalloonOk("", "4096"), "벌룬 빈 값");

        c.Is(GuestInputChecks.IsCtIpv4("dhcp"), "CT ip dhcp");
        c.Is(GuestInputChecks.IsCtIpv4("manual"), "CT ip manual");
        c.Is(GuestInputChecks.IsCtIpv4("192.168.10.53/24"), "CT ip CIDR");
        c.Is(!GuestInputChecks.IsCtIpv4("192.168.10.53"), "접두 길이 없음 거부");
        c.Is(!GuestInputChecks.IsCtIpv4("192.168.10.53/33"), "접두 33 거부");
        c.Is(!GuestInputChecks.IsCtIpv4("2001:db8::1/64"), "IPv6 는 ip 칸이 아님");
        c.Is(GuestInputChecks.IsCidr("2001:db8::1/64", AddressFamily.InterNetworkV6), "IPv6 CIDR");
        c.Is(!GuestInputChecks.IsCidr("2001:db8::1/129", AddressFamily.InterNetworkV6), "IPv6 접두 129 거부");
    }

    public static void DigestAndUpload(Checker c)
    {
        var mismatch = new ProxmoxApiException(500,
            "{\"data\":null,\"message\":\"detected modified configuration - file changed by other user? Try again.\\n\"}");
        c.Is(mismatch.IsDigestMismatch, "digest 불일치 응답을 알아본다");
        c.Is(!new ProxmoxApiException(500, "{\"data\":null,\"message\":\"VM is locked\"}").IsDigestMismatch,
            "다른 오류는 아니다");

        var pending = GuestPendingConfig.FromRows(
        [
            new Dictionary<string, string> { ["key"] = "digest", ["value"] = "abc123" },
            new Dictionary<string, string> { ["key"] = "memory", ["value"] = "2048", ["pending"] = "4096" }
        ]);
        c.Equal("abc123", pending.Digest, "pending 응답의 digest");
        c.Equal("4096", pending.Effective["memory"], "대기 중 값 반영");
        var form = pending.AddDigest(new Dictionary<string, string> { ["cores"] = "2" });
        c.Equal("abc123", form.GetValueOrDefault("digest"), "쓰기 폼에 digest");
        c.Is(!GuestPendingConfig.Empty.AddDigest(new Dictionary<string, string>()).ContainsKey("digest"),
            "digest 가 없으면 보내지 않는다");

        c.Is(ProxmoxApiClient.IsUploadExtensionOk("iso", "debian-12.iso"), ".iso");
        c.Is(ProxmoxApiClient.IsUploadExtensionOk("iso", "OPNsense.IMG"), ".IMG(대소문자 무관)");
        c.Is(!ProxmoxApiClient.IsUploadExtensionOk("iso", "debian.iso.zip"), ".zip 거부");
        c.Is(!ProxmoxApiClient.IsUploadExtensionOk("iso", ".iso"), "이름 없는 .iso 거부");
        c.Is(ProxmoxApiClient.IsUploadExtensionOk("vztmpl", "debian-12-standard.tar.zst"), ".tar.zst");
        c.Is(ProxmoxApiClient.IsUploadExtensionOk("vztmpl", "alpine.tar"), ".tar");
        c.Is(!ProxmoxApiClient.IsUploadExtensionOk("vztmpl", "alpine.tgz"), ".tgz 는 서버가 받지 않는다");
    }
}

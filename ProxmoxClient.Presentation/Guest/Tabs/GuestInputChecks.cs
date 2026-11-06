using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace ProxmoxClient.Presentation.Guest.Tabs;

/// <summary>
///     게스트 관리 화면들이 함께 쓰는 입력 검사 — 서버(PVE)가 거절할 값을 보내기 전에 막는다.
///     규칙은 PVE 스키마를 따른다(pve-common JSONSchema·qemu-server·pve-container).
/// </summary>
public static partial class GuestInputChecks
{
    /// <summary>설명(description)의 최대 길이 — VM·CT 모두 8 KiB. 한글은 글자당 3바이트라 바이트로 센다.</summary>
    public const int DescriptionMaxBytes = 8 * 1024;

    /// <summary>스냅숏 이름 최대 길이(pve-snapshot-name maxLength).</summary>
    public const int SnapshotNameMax = 40;

    // pve_verify_mac_addr: 유니캐스트만(첫 바이트의 I/G 비트가 0 — 두 번째 글자가 짝수)
    [GeneratedRegex("^[0-9a-fA-F][02468aceACE](:[0-9a-fA-F]{2}){5}$")]
    private static partial Regex UnicastMac();

    // pve-configid: 글자로 시작, 글자·숫자·_·- 로 2글자 이상
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]+$")]
    private static partial Regex ConfigId();

    /// <summary>VM 네트워크 MAC — 비우면 서버가 만든다. 유니캐스트 형식(xx:xx:xx:xx:xx:xx)이어야 한다.</summary>
    public static bool IsMacOrEmpty(string mac)
    {
        mac = mac.Trim();
        return mac.Length == 0 || UnicastMac().IsMatch(mac);
    }

    /// <summary>스냅숏 이름 — 글자로 시작, 글자·숫자·_·- 만, 2~40자.</summary>
    public static bool IsSnapshotName(string name)
    {
        return name.Length <= SnapshotNameMax && ConfigId().IsMatch(name);
    }

    /// <summary>설명이 8 KiB(UTF-8) 이하인가.</summary>
    public static bool IsDescriptionLengthOk(string text)
    {
        return Encoding.UTF8.GetByteCount(text) <= DescriptionMaxBytes;
    }

    /// <summary>CT 네트워크 ip — 빈 값, dhcp, manual, 또는 IPv4 주소/접두 길이(0~32).</summary>
    public static bool IsCtIpv4(string ip)
    {
        ip = ip.Trim();
        return ip.Length == 0 || ip is "dhcp" or "manual" || IsCidr(ip, AddressFamily.InterNetwork);
    }

    /// <summary>주소/접두 길이(CIDR) — family 의 주소이고 접두 길이가 범위 안이어야 한다.</summary>
    public static bool IsCidr(string text, AddressFamily family)
    {
        var slash = text.IndexOf('/');
        if (slash <= 0) return false;
        if (!IPAddress.TryParse(text[..slash], out var address) || address.AddressFamily != family) return false;
        var max = family == AddressFamily.InterNetwork ? 32 : 128;
        return int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)
               && prefix <= max;
    }

    /// <summary>
    ///     벌룬(최소 메모리) — 0 은 끄기. 그 외에는 메모리보다 클 수 없다(서버가 "balloon value too large" 로 거절).
    ///     둘 다 숫자일 때만 비교하고, 아니면 통과(다른 검사가 막는다).
    /// </summary>
    public static bool IsBalloonOk(string balloon, string memory)
    {
        if (!long.TryParse(balloon.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var b)) return true;
        if (!long.TryParse(memory.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var m)) return true;
        return b == 0 || b <= m;
    }

    /// <summary>
    ///     디스크 크기(GiB) — 소수점만 허용하고 지수(1e3)·천 단위 구분·부호는 받지 않는다. 문화권과 무관하게 '.' 이다.
    ///     0 보다 커야 한다.
    /// </summary>
    public static bool TryParseGiB(string text, out decimal value)
    {
        return decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                   out value) && value > 0;
    }

    /// <summary>
    ///     CT 마운트 경로 — 절대 경로, 루트(/) 자체가 아님, property string 을 깨는 쉼표·줄바꿈 없음,
    ///     PVE 가 막는 /./ · /../ · 끝의 /. · /.. 없음(verify_lxc_mp_string).
    /// </summary>
    public static bool IsMountPath(string path)
    {
        if (path.Length < 2 || path[0] != '/') return false;
        if (!Core.Models.PropertyString.IsValidValue(path)) return false;
        return !(path.Contains("/./", StringComparison.Ordinal) || path.Contains("/../", StringComparison.Ordinal)
                 || path.EndsWith("/.", StringComparison.Ordinal) || path.EndsWith("/..", StringComparison.Ordinal));
    }
}

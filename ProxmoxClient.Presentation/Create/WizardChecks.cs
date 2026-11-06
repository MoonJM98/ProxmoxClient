using System.Text.RegularExpressions;

namespace ProxmoxClient.Presentation.Create;

/// <summary>두 마법사의 네트워크 단계가 함께 쓰는 입력 검사 — 서버가 거절할 값을 미리 막는다.</summary>
internal static partial class WizardChecks
{
    [GeneratedRegex("^[0-9a-fA-F]{2}(:[0-9a-fA-F]{2}){5}$")]
    private static partial Regex MacPattern();

    /// <summary>브리지·VLAN(1~4094)·MAC(비우면 자동) — 문제가 있으면 문구, 없으면 null.</summary>
    public static string? Network(string bridge, string vlan, string mac)
    {
        if (bridge.Length == 0) return UiText.T("Wz_PickBridge");
        if (vlan.Trim().Length > 0 && !(int.TryParse(vlan.Trim(), out var tag) && tag is >= 1 and <= 4094))
            return UiText.T("Wz_VlanRange");
        return mac.Trim().Length == 0 || MacPattern().IsMatch(mac.Trim()) ? null : UiText.T("Wz_BadMac");
    }
}

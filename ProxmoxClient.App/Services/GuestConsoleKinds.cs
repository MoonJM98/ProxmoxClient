using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Services;

/// <summary>
///     VM 설정으로 본 쓸 수 있는 콘솔 종류 — SPICE(디스플레이 qxl), 직렬 터미널(직렬 포트), RDP(디스플레이 rdp).
///     설정을 못 읽으면(VM.Audit 권한 없음 등) SPICE·직렬은 있다고 보고 서버가 판단하게 한다. RDP 는 기본으로 고르지는
///     않지만(아직 정식 PVE 기능이 아님) 메뉴에는 보여 직접 고를 수 있게 한다(<see cref="Known" /> 가 false).
/// </summary>
internal sealed record GuestConsoleKinds(bool Spice, bool Serial, bool Rdp, bool Known = true)
{
    private static readonly GuestConsoleKinds Unknown = new(true, true, false, false);

    public static async Task<GuestConsoleKinds> DetectAsync(ProxmoxApiClient api, PveResource guest)
    {
        try
        {
            var config = await api.GetGuestConfigAsync(guest.Node, guest.Kind, guest.VmId);
            var display = DisplayType(config.TryGetValue("vga", out var vga) ? vga : null);
            return new GuestConsoleKinds(
                display.StartsWith("qxl", StringComparison.Ordinal),
                config.Keys.Any(key => key.StartsWith("serial", StringComparison.Ordinal)),
                display == "rdp");
        }
        // 인증서 거부도 — 세션 중 서버 인증서가 바뀌면 메뉴(async void)에서 전역 오류 창으로 올라간다. 연결 끊기는
        // 메인 새로 고침이 맡는다
        catch (Exception ex) when (ex is ProxmoxApiException or System.Net.Http.HttpRequestException
                                       or TaskCanceledException or ObjectDisposedException
                                       or CertificateTrustException)
        {
            App.Log($"[콘솔 종류] {guest.VmId} 설정 읽기 실패: {ex.Message}");
            return Unknown;
        }
    }

    /// <summary>
    ///     vga 값("qxl,memory=32" · "type=rdp,clipboard=vnc")의 디스플레이 종류 — 이름 없는 첫 값 또는 type=.
    ///     비어 있으면 기본(std).
    /// </summary>
    public static string DisplayType(string? vga)
    {
        foreach (var part in (vga ?? string.Empty).Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("type=", StringComparison.Ordinal)) return part["type=".Length..];
            if (part.Length > 0 && !part.Contains('=')) return part;
        }

        return "std";
    }
}

using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views;

/// <summary>
///     클립보드 공유 — 웹 UI noVNC 와 같은 조건: VM 디스플레이에 clipboard=vnc 가 켜져 있어야 한다(게스트에는 spice-vdagent).
///     콘솔 설정의 "클립보드 자동 동기화"가 켜져 있으면 이 창으로 돌아오는 순간 PC 클립보드를 게스트로 보내고,
///     떠나는 순간 게스트 클립보드를 받아 PC 에 넣는다(게스트에서 복사하면 그때도 바로 온다).
///     꺼져 있으면 클립보드 버튼으로만 PC → 게스트를 보낸다. VM 이 지원하지 않으면 버튼을 막고 켜는 방법을 툴팁으로 알린다.
/// </summary>
public partial class ConsoleWindow
{
    /// <summary>이 VM 이 VNC 클립보드를 쓴다(clipboard=vnc).</summary>
    private bool _clipboardShared;

    /// <summary>마지막으로 주고받은 글 — 같은 글을 되돌려 보내지 않는다.</summary>
    private string? _lastClipboardSync;

    private bool AutoClipboard => _clipboardShared && _settings.AutoClipboardSync && _session?.IsConnected == true;

    /// <summary>
    ///     연결 뒤 VM 디스플레이 설정을 확인한다 — clipboard=vnc(클립보드 공유), qxl(SPICE 버튼).
    ///     못 읽으면 클립보드는 꺼진 것으로 보고 SPICE 버튼은 그대로 둔다(서버가 판단).
    /// </summary>
    private async Task DetectClipboardAsync()
    {
        if (IsRdp)
        {
            // RDP 는 VNC 클립보드·SPICE 와 상관없다 — 클립보드는 세션이 자동으로 주고받는다(버튼은 안내만)
            _clipboardShared = false;
            BtnSpice.Visibility = Visibility.Collapsed;
            UpdateClipboardButton(_session?.IsConnected == true);
            return;
        }

        try
        {
            var config = await _api.GetGuestConfigAsync(_node, ResourceKind.Qemu, _vmid);
            var vga = config.TryGetValue("vga", out var value) ? value : string.Empty;
            _clipboardShared = vga.Contains("clipboard=vnc", StringComparison.OrdinalIgnoreCase);
            // SPICE 는 디스플레이가 SPICE(qxl…) 일 때만 열린다 — 아니면 버튼을 숨긴다(웹 UI 도 막는다)
            BtnSpice.Visibility = GuestConsoleKinds.DisplayType(vga).StartsWith("qxl", StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is ProxmoxApiException or System.Net.Http.HttpRequestException
                                       or TaskCanceledException or ObjectDisposedException)
        {
            App.Log($"[콘솔 {_vmid}] 클립보드 설정 확인 실패: {ex.Message}");
            _clipboardShared = false;
            BtnSpice.Visibility = Visibility.Visible; // 모르면 두고 서버가 판단한다
        }

        UpdateClipboardButton(_session?.IsConnected == true);
        if (IsActive) SyncClipboardToGuest(); // 연결하자마자 지금 PC 클립보드를 맞춘다
    }

    private void UpdateClipboardButton(bool connected)
    {
        BtnClip.IsEnabled = connected && _clipboardShared;
        var rdpShared = _session is Core.Rdp.ProxmoxRdpSession { ClipboardActive: true };
        BtnClip.ToolTip = Loc.T(IsRdp
            ? rdpShared ? "Console_ClipRdp" : "Console_ClipRdpOff"
            : _clipboardShared ? "Console_ClipShared" : "Console_ClipNeedsVnc");
    }

    /// <summary>게스트 클립보드가 왔다(게스트에서 복사했거나 떠날 때 요청한 답) — 자동이면 PC 클립보드에 넣는다.</summary>
    private void OnGuestClipboard(string text)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || !AutoClipboard || text.Length == 0 || text == _lastClipboardSync) return;

            try
            {
                Clipboard.SetText(text);
                _lastClipboardSync = text;
                SetState(Loc.T("Console_ClipFromGuest"));
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                SetState(Loc.T("ConsoleWindow_M10", ex.Message)); // 다른 프로그램이 클립보드를 잡고 있다
            }
        });
    }

    /// <summary>이 창으로 돌아왔다 — 자동이면 PC 에서 새로 복사한 글을 게스트 클립보드로 보낸다.</summary>
    private void SyncClipboardToGuest()
    {
        if (!AutoClipboard) return;

        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (text.Length == 0 || text == _lastClipboardSync) return;

            _lastClipboardSync = text;
            _ = _session!.SendClipboardAsync(text);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            App.Log($"[콘솔 {_vmid}] 클립보드 읽기 실패: {ex.Message}");
        }
    }

    /// <summary>이 창을 떠난다 — 자동이면 게스트 클립보드를 요청해 PC 에 넣는다(답은 <see cref="OnGuestClipboard" />).</summary>
    private void RequestGuestClipboard()
    {
        if (AutoClipboard) _ = _session!.RequestClipboardAsync();
    }
}

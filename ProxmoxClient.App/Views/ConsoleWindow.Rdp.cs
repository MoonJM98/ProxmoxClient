using System.Windows.Media;
using System.Windows.Threading;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.App.Views;

/// <summary>
///     RDP 콘솔(VM 디스플레이 rdp) — 화면·입력은 VNC 와 같은 길을 쓰고, 커서·축소 설정은 RDP 값을 쓴다.
///     맞춤 모드에서 창 크기가 바뀌면(잠깐 멈춘 뒤) 게스트 해상도를 창에 맞춰 달라고 요청한다.
/// </summary>
public partial class ConsoleWindow
{
    /// <summary>창 크기 조절이 멈춘 뒤 이만큼 지나면 해상도를 요청한다 — 끄는 동안 매번 보내지 않게.</summary>
    private static readonly TimeSpan ResizeDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>화면 영역을 아직 모를 때 처음 요청할 해상도.</summary>
    private const int DefaultDesktopWidth = 1280;

    private const int DefaultDesktopHeight = 800;

    private readonly ConsoleProtocol _protocol;
    private DispatcherTimer? _resizeTimer;

    private bool IsRdp => _protocol == ConsoleProtocol.Rdp;

    /// <summary>이 콘솔 종류의 커서 표시 방식.</summary>
    private ConsoleSettings.LocalCursorMode CursorMode => IsRdp ? _settings.RdpLocalCursor : _settings.LocalCursor;

    /// <summary>이 콘솔 종류의 부드러운 축소 설정.</summary>
    private bool SmoothScaling => IsRdp ? _settings.RdpSmoothScaling : _settings.SmoothScaling;

    /// <summary>창 크기에 맞춘 해상도를 쓸지 — RDP·맞춤 모드·설정이 모두 켜져 있을 때.</summary>
    private bool FitsGuestToWindow => IsRdp && _fitMode && _settings.RdpDynamicResolution;

    /// <summary>화면 영역의 실제 픽셀 크기(DPI 반영) — 아직 배치 전이면 기본 해상도.</summary>
    private (int Width, int Height) DesiredDesktopSize()
    {
        if (!FitsGuestToWindow || ConsoleScroll.ActualWidth < 1 || ConsoleScroll.ActualHeight < 1)
            return (DefaultDesktopWidth, DefaultDesktopHeight);

        var dpi = VisualTreeHelper.GetDpi(this);
        return ((int)(ConsoleScroll.ActualWidth * dpi.DpiScaleX), (int)(ConsoleScroll.ActualHeight * dpi.DpiScaleY));
    }

    private void QueueDesktopResize()
    {
        if (!FitsGuestToWindow || _session?.IsConnected != true) return;

        if (_resizeTimer is null)
        {
            _resizeTimer = new DispatcherTimer { Interval = ResizeDebounce };
            _resizeTimer.Tick += (_, _) =>
            {
                _resizeTimer.Stop();
                if (_closed || !FitsGuestToWindow || _session?.IsConnected != true) return;

                var (width, height) = DesiredDesktopSize();
                _session.RequestDesktopSize(width, height);
            };
        }

        _resizeTimer.Stop();
        _resizeTimer.Start();
    }
}

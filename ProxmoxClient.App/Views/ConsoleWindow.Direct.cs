using System.Drawing;
using System.Windows;
using System.Windows.Media;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Rendering;
using Point = System.Windows.Point;

namespace ProxmoxClient.App.Views;

/// <summary>
///     GPU 로 바로 그리기(<see cref="DirectScreenRenderer" />) — 켜져 있으면 콘솔 화면 Image 는 투명하게 두고
///     자리·마우스 좌표 계산과 입력만 맡는다. 그 위(스크롤 뷰포트 크기)에 자식 창을 겹쳐 같은 자리에 그린다.
///     GPU 를 쓸 수 없으면 알리고 WPF 그리기로 돌아간다.
/// </summary>
public partial class ConsoleWindow
{
    private DirectScreenHost? _directHost;
    private DirectScreenRenderer? _direct;
    private int _directSizeSyncQueued;

    /// <summary>설정에 맞춰 켜고 끈다(창을 띄울 때·설정을 저장했을 때).</summary>
    private void ApplyDirectRendering()
    {
        if (_settings.DirectRendering) EnableDirect();
        else DisableDirect(false);
    }

    private void EnableDirect()
    {
        if (_directHost is not null || _closed) return;

        var host = new DirectScreenHost();
        host.RendererCreated += renderer =>
        {
            renderer.Failed += _ => Dispatcher.BeginInvoke(() =>
            {
                if (!_closed && ReferenceEquals(_directHost, host)) DisableDirect(true);
            });
            renderer.SetSource(() => _session is { } s ? (s.Framebuffer, s.Width, s.Height) : (null, 0, 0));
            _direct = renderer;
            ScreenImage.Opacity = 0; // 자리·입력만 — 보이는 화면은 GPU 가 그린다
            UpdateOverlayImage();
            UpdateDirectLayout();
        };
        _directHost = host;
        DirectSlot.Children.Add(host);
        UpdateDirectSlot();
    }

    /// <summary>창을 닫을 때 — 렌더 스레드·GPU 자원을 놓는다(닫힌 창의 자식 창은 저절로 지워지지 않는다).</summary>
    private void CloseDirect()
    {
        if (_directHost is not { } host) return;

        _directHost = null;
        _direct = null;
        host.Dispose();
    }

    private void DisableDirect(bool failed)
    {
        if (_directHost is not { } host) return;

        _directHost = null;
        _direct = null;
        DirectSlot.Children.Remove(host);
        host.Dispose();
        ScreenImage.Opacity = 1;
        UpdateOverlayImage();
        if (_fbWidth > 0) QueueFrameFlush(0, 0, _fbWidth, _fbHeight); // WPF 비트맵은 그동안 쓰지 않았다
        if (failed) SetState(Loc.T("Console_DirectFailed"));
    }

    /// <summary>
    ///     읽기 스레드의 프레임 알림 — GPU 로 그리는 중이면 렌더러에 넘기고 true.
    ///     해상도가 바뀌었으면 UI 쪽 크기(자리·좌표 계산용 Image)도 맞춘다.
    /// </summary>
    private bool TryQueueDirectFrame(int x, int y, int w, int h)
    {
        if (_direct is not { } direct) return false;

        direct.Invalidate(x, y, w, h);
        if (_session is { } session && (session.Width != _fbWidth || session.Height != _fbHeight)
                                    && Interlocked.Exchange(ref _directSizeSyncQueued, 1) == 0)
            Dispatcher.BeginInvoke(() =>
            {
                Volatile.Write(ref _directSizeSyncQueued, 0);
                if (_session is { IsConnected: true } current) EnsureBitmap(current.Width, current.Height);
                UpdateDirectLayout();
            });
        return true;
    }

    /// <summary>자식 창을 스크롤 뷰포트(스크롤 막대 제외)에 맞춘다 — 스크롤 막대·도구 모음을 덮지 않게.</summary>
    private void UpdateDirectSlot()
    {
        DirectSlot.Width = Math.Max(0, ConsoleScroll.ViewportWidth);
        DirectSlot.Height = Math.Max(0, ConsoleScroll.ViewportHeight);
        // 정지 안내처럼 화면 위에 WPF 를 덮을 때는 숨긴다(자식 창 위에는 WPF 가 그려지지 않는다)
        DirectSlot.Visibility = StoppedPanel.IsVisible ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>프레임버퍼를 그릴 자리 — 투명한 Image 가 그려질 자리와 같게(맞춤 배율·가운데·스크롤 반영).</summary>
    private void UpdateDirectLayout()
    {
        if (_direct is not { } direct || _fbWidth == 0 || _fbHeight == 0 || !ScreenImage.IsVisible
            || PresentationSource.FromVisual(DirectSlot) is null)
            return;

        var scale = DisplayScale();
        var width = _fbWidth * scale;
        var height = _fbHeight * scale;
        var offsetX = _fitMode ? (ScreenImage.ActualWidth - width) / 2 : 0;
        var offsetY = _fitMode ? (ScreenImage.ActualHeight - height) / 2 : 0;
        var origin = ScreenImage.TranslatePoint(new Point(offsetX, offsetY), DirectSlot);
        var dpi = VisualTreeHelper.GetDpi(this);
        // 물리 픽셀에 맞춰 둔다 — 1:1 에서 반 픽셀 보간으로 흐려지지 않게
        var destination = new RectangleF((float)Math.Round(origin.X * dpi.DpiScaleX),
            (float)Math.Round(origin.Y * dpi.DpiScaleY), (float)Math.Round(width * dpi.DpiScaleX),
            (float)Math.Round(height * dpi.DpiScaleY));
        direct.SetLayout(destination, _fitMode && SmoothScaling);
    }

    /// <summary>"둘 다 보기" 의 게스트 커서를 GPU 화면에 — 위치는 ScreenHost 기준(WPF 단위).</summary>
    private void MoveDirectCursor(DirectScreenRenderer direct, Point position, bool visible)
    {
        if (!visible || PresentationSource.FromVisual(DirectSlot) is null)
        {
            direct.MoveCursor(null, 1);
            return;
        }

        var at = ScreenHost.TranslatePoint(position, DirectSlot);
        var dpi = VisualTreeHelper.GetDpi(this);
        direct.MoveCursor(new PointF((float)(at.X * dpi.DpiScaleX), (float)(at.Y * dpi.DpiScaleY)),
            (float)(DisplayScale() * dpi.DpiScaleX));
    }
}

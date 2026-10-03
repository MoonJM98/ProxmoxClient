using System.Windows.Input;

namespace ProxmoxClient.App.Views;

/// <summary>마우스 — 화면 좌표를 게스트 좌표로 바꿔 포인터 이벤트를 보낸다.</summary>
public partial class ConsoleWindow
{
    private (int X, int Y) _lastPointer; // 마지막으로 보낸 게스트 좌표 — 캡처를 잃었을 때 그 자리에서 뗀다

    private void SendPointer(int extraMask)
    {
        if (_session?.IsConnected != true) return;

        var pos = ToVncCoordinates(Mouse.GetPosition(ScreenImage));
        _lastPointer = pos;
        _ = _session.SendPointerAsync(extraMask, pos.X, pos.Y);
    }

    /// <summary>
    ///     마우스 캡처를 잃었다(다른 창이 포커스를 가져감 등) — 눌린 버튼이 있으면 마지막 자리에서 게스트에도 뗀다.
    ///     로컬 상태만 지우면 게스트는 끌기를 계속하다 마우스가 돌아온 엉뚱한 자리에 놓는다.
    /// </summary>
    private void OnScreenLostMouseCapture()
    {
        if (_pointerMask == 0) return;

        _pointerMask = 0;
        if (_session?.IsConnected == true) _ = _session.SendPointerAsync(0, _lastPointer.X, _lastPointer.Y);
    }
    private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        ScreenImage.CaptureMouse();
        var mask = _pointerMask | ButtonMask(e.ChangedButton);
        _pointerMask = mask;
        SendPointer(mask);
        e.Handled = true;
    }
    private void OnImageMouseUp(object sender, MouseButtonEventArgs e)
    {
        var mask = _pointerMask & ~ButtonMask(e.ChangedButton);
        _pointerMask = mask;
        SendPointer(mask);
        if (mask == 0) ScreenImage.ReleaseMouseCapture();

        e.Handled = true;
    }
    private void OnImageMouseEnter(object sender, MouseEventArgs e)
    {
        if (_session?.IsConnected == true) SendPointer(_pointerMask); // 진입 시점 위치 동기화
        MoveCursorOverlay(e.GetPosition(ScreenHost));
    }
    private void OnImageMouseLeave(object sender, MouseEventArgs e)
    {
        // 영역을 벗어나도 캡처 중(드래그)이면 계속 전송 — CaptureMouse 유지
        MoveCursorOverlay(e.GetPosition(ScreenHost));
    }
    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        // VNC는 절대 좌표 방식 — 호버 이동도 항상 전송해야 게스트 커서가 따라온다
        MoveCursorOverlay(e.GetPosition(ScreenHost));
        SendPointer(_pointerMask);
        e.Handled = true;
    }
    private void OnImageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        const int wheelUp = 8;
        const int wheelDown = 16;
        var wheel = e.Delta > 0 ? wheelUp : wheelDown;
        SendPointer(_pointerMask | wheel);
        SendPointer(_pointerMask);
        e.Handled = true;
    }
    private static int ButtonMask(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => 1,
            MouseButton.Middle => 2,
            MouseButton.Right => 4,
            // RFB 마스크 8/16 은 휠 위/아래 — 뒤로/앞으로 버튼을 여기에 매핑하면 스크롤로 동작하므로 전송하지 않는다
            _ => 0
        };
    }
}

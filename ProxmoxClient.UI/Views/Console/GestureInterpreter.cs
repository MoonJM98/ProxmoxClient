using Avalonia;

namespace ProxmoxClient.UI.Views.Console;

/// <summary>터치 조작 방식 — 마우스 모드: 화면이 터치패드(커서를 움직여 클릭). 터치 모드: 누른 곳을 바로 클릭.</summary>
public enum TouchInputMode
{
    Mouse,
    Touch
}

/// <summary>원격 포인터로 보낼 곳(RFB PointerEvent).</summary>
public interface IRemotePointer
{
    /// <param name="buttons">RFB 버튼 마스크 — 1 왼쪽 · 2 가운데 · 4 오른쪽 · 8/16 휠 위/아래 · 32/64 휠 왼쪽/오른쪽.</param>
    void Send(int buttons, int x, int y);
}

/// <summary>
///     터치 제스처 → 원격 마우스 이벤트. 화면 그리기와 떨어진 순수 로직이라 조작성 검사가 그대로 시험한다.
///     규칙은 널리 쓰이는 모바일 원격 앱(RealVNC Viewer 마우스 모드, AVNC 등)과 같게 했다:
///     <list type="bullet">
///         <item>한 손가락 탭 = 왼쪽 클릭(마우스 모드: 커서 위치 · 터치 모드: 누른 곳), 빠른 두 번 탭 = 더블 클릭</item>
///         <item>두 손가락 탭 = 오른쪽 클릭, 세 손가락 탭 = 가운데 클릭</item>
///         <item>길게 누르기 = 오른쪽 클릭, 길게 누른 뒤 끌기 = 왼쪽 버튼 끌기(선택·창 이동)</item>
///         <item>한 손가락 끌기 = 커서 이동(마우스 모드) / 왼쪽 버튼 끌기(터치 모드)</item>
///         <item>두 손가락 끌기 = 스크롤(세로·가로), 벌리기·오므리기 = 확대·축소(+ 이동)</item>
///     </list>
/// </summary>
public sealed class GestureInterpreter(Viewport viewport, IRemotePointer remote)
{
    public const int LongPressMs = 450;
    public const double TapSlop = 12;
    public const int MultiTapMs = 350;
    public const double ScrollStep = 28;
    public const double PinchThreshold = 24;

    private const int Left = 1, Middle = 2, Right = 4, WheelUp = 8, WheelDown = 16, WheelLeft = 32, WheelRight = 64;

    private readonly Dictionary<long, Finger> _fingers = [];
    private int _buttons;
    private Gesture _gesture = Gesture.None;
    private int _maxFingers;
    private long _gestureStart;
    private double _pinchStartDistance;
    private Point _lastCentroid;
    private double _lastDistance;
    private Vector _scrollRemainder;
    private bool _longPressFired;

    private enum Gesture
    {
        None,
        Pending,
        CursorMove,
        Drag,
        Scroll,
        Pinch,
        Done
    }

    public TouchInputMode Mode { get; set; } = TouchInputMode.Mouse;

    /// <summary>커서 위치(원격 좌표) — 마우스 모드에서 화면에 그린다.</summary>
    public Point Cursor { get; private set; }

    /// <summary>커서 이동 배율 — 1 이면 손가락이 움직인 화면 거리만큼(빠르게 움직이면 가속).</summary>
    public double CursorSpeed { get; set; } = 1.0;

    public void ResetCursor(Point remote)
    {
        Cursor = remote;
    }

    public void Down(long id, Point view, long ms)
    {
        _fingers[id] = new Finger(view, view, ms);
        _maxFingers = Math.Max(_maxFingers, _fingers.Count);
        if (_fingers.Count == 1)
        {
            _gesture = Gesture.Pending;
            _gestureStart = ms;
            _longPressFired = false;
            return;
        }

        // 두 번째 손가락: 한 손가락 동작을 거두고 두 손가락 제스처로
        if (_gesture == Gesture.Drag) Release();
        if (_gesture is Gesture.Pending or Gesture.CursorMove or Gesture.Drag or Gesture.None)
            _gesture = Gesture.Pending;
        _lastCentroid = Centroid();
        _lastDistance = _pinchStartDistance = Distance();
        _scrollRemainder = default;
    }

    public void Move(long id, Point view, long ms)
    {
        if (!_fingers.TryGetValue(id, out var finger)) return;

        var delta = view - finger.Last;
        _fingers[id] = finger with { Last = view };

        if (_fingers.Count >= 2)
        {
            MoveMulti();
            return;
        }

        var moved = Length(view - finger.Start) > TapSlop;
        switch (_gesture)
        {
            case Gesture.Pending when moved:
                if (Mode == TouchInputMode.Touch)
                {
                    // 터치 모드: 누른 곳에서 왼쪽 버튼을 누른 채 끈다
                    Point(finger.Start, 0);
                    Press(Left);
                    _gesture = Gesture.Drag;
                    Point(view, _buttons);
                }
                else if (_longPressFired || ms - _gestureStart >= LongPressMs)
                {
                    // 마우스 모드에서 길게 누른 뒤 끌기 = 왼쪽 버튼 끌기
                    Press(Left);
                    _gesture = Gesture.Drag;
                    MoveCursor(delta, ms - finger.Time);
                }
                else
                {
                    _gesture = Gesture.CursorMove;
                    MoveCursor(view - finger.Start, ms - finger.Time);
                }

                break;
            case Gesture.CursorMove:
                MoveCursor(delta, ms - finger.Time);
                break;
            case Gesture.Drag:
                if (Mode == TouchInputMode.Touch) Point(view, _buttons);
                else MoveCursor(delta, ms - finger.Time);
                break;
        }

        _fingers[id] = _fingers[id] with { Time = ms };
    }

    public void Up(long id, Point view, long ms)
    {
        if (!_fingers.Remove(id, out var finger)) return;

        if (_fingers.Count > 0)
        {
            // 여러 손가락 중 하나를 뗐다 — 나머지를 뗄 때 마무리
            if (_gesture == Gesture.Pending && _maxFingers >= 2)
                _gesture = Gesture.Pending;
            return;
        }

        var duration = ms - _gestureStart;
        switch (_gesture)
        {
            case Gesture.Pending when _maxFingers >= 3:
                Click(TapPoint(finger), Middle);
                break;
            case Gesture.Pending when _maxFingers == 2:
                Click(TapPoint(finger), Right);
                break;
            case Gesture.Pending when _longPressFired || duration >= LongPressMs:
                Click(TapPoint(finger), Right);
                break;
            case Gesture.Pending:
                Click(TapPoint(finger), Left);
                break;
            case Gesture.Drag:
                Release();
                break;
        }

        _gesture = Gesture.None;
        _maxFingers = 0;
    }

    /// <summary>
    ///     주기적으로 불러 길게 누르기를 손을 떼기 전에 알아챈다 — <see cref="LongPressArmed" /> 가 켜지면 화면이
    ///     "떼면 오른쪽 클릭 · 끌면 선택" 표시를 보여 준다. 클릭은 손을 뗄 때 보낸다(끌기와 구분하려고).
    /// </summary>
    public bool Tick(long ms)
    {
        if (_gesture != Gesture.Pending || _fingers.Count != 1 || _longPressFired || _maxFingers != 1) return false;

        var finger = _fingers.Values.First();
        if (ms - _gestureStart < LongPressMs || Length(finger.Last - finger.Start) > TapSlop) return false;

        _longPressFired = true;
        return true;
    }

    /// <summary>길게 누른 상태(아직 손을 떼지 않음).</summary>
    public bool LongPressArmed => _longPressFired && _gesture == Gesture.Pending && _fingers.Count == 1;

    /// <summary>제스처를 강제로 끝낸다(화면을 떠날 때 버튼이 눌린 채 남지 않게).</summary>
    public void Cancel()
    {
        if (_buttons != 0) Release();
        _fingers.Clear();
        _gesture = Gesture.None;
        _maxFingers = 0;
    }

    private void MoveMulti()
    {
        var centroid = Centroid();
        var distance = Distance();
        if (_gesture == Gesture.Pending)
        {
            if (Math.Abs(distance - _pinchStartDistance) > PinchThreshold) _gesture = Gesture.Pinch;
            else if (Length(centroid - _lastCentroid) > TapSlop && _fingers.Count == 2) _gesture = Gesture.Scroll;
            else return;
        }

        switch (_gesture)
        {
            case Gesture.Pinch:
                if (_lastDistance > 0) viewport.ZoomAt(distance / _lastDistance, centroid);
                viewport.PanBy(centroid - _lastCentroid);
                break;
            case Gesture.Scroll:
                _scrollRemainder += centroid - _lastCentroid;
                var at = Mode == TouchInputMode.Mouse ? Cursor : viewport.ToRemote(centroid);
                // 손가락을 올리면(위로 밀면) 내용이 위로 = 아래로 스크롤(휴대폰과 같은 방향)
                while (_scrollRemainder.Y <= -ScrollStep) { Wheel(WheelDown, at); _scrollRemainder += new Vector(0, ScrollStep); }
                while (_scrollRemainder.Y >= ScrollStep) { Wheel(WheelUp, at); _scrollRemainder -= new Vector(0, ScrollStep); }
                while (_scrollRemainder.X <= -ScrollStep) { Wheel(WheelRight, at); _scrollRemainder += new Vector(ScrollStep, 0); }
                while (_scrollRemainder.X >= ScrollStep) { Wheel(WheelLeft, at); _scrollRemainder -= new Vector(ScrollStep, 0); }
                break;
        }

        _lastCentroid = centroid;
        _lastDistance = distance;
    }

    private Point TapPoint(Finger finger)
    {
        return Mode == TouchInputMode.Mouse ? Cursor : viewport.ToRemote(finger.Start);
    }

    private void MoveCursor(Vector viewDelta, long elapsedMs)
    {
        // 빠르게 움직일수록 더 멀리(가속) — 작은 화면에서도 큰 원격 화면을 끝까지 옮길 수 있게
        var speed = elapsedMs > 0 ? Length(viewDelta) / elapsedMs : 0; // px/ms
        var accel = Math.Clamp(1 + (speed - 0.3) * 1.2, 1, 3);
        var remoteDelta = viewDelta / viewport.Scale * CursorSpeed * accel;
        var next = Cursor + remoteDelta;
        Cursor = new Point(Math.Clamp(next.X, 0, Math.Max(0, viewport.Remote.Width - 1)),
            Math.Clamp(next.Y, 0, Math.Max(0, viewport.Remote.Height - 1)));
        viewport.EnsureVisible(Cursor);
        Send(_buttons, Cursor);
    }

    private void Point(Point view, int buttons)
    {
        Cursor = viewport.ToRemote(view);
        Send(buttons, Cursor);
    }

    private void Click(Point remotePoint, int button)
    {
        Cursor = remotePoint;
        Send(0, remotePoint);
        Send(button, remotePoint);
        Send(0, remotePoint);
    }

    private void Wheel(int wheel, Point at)
    {
        Send(_buttons | wheel, at);
        Send(_buttons, at);
    }

    private void Press(int button)
    {
        _buttons |= button;
        Send(_buttons, Cursor);
    }

    private void Release()
    {
        _buttons = 0;
        Send(0, Cursor);
    }

    private void Send(int buttons, Point remotePoint)
    {
        remote.Send(buttons, (int)Math.Round(remotePoint.X), (int)Math.Round(remotePoint.Y));
    }

    private Point Centroid()
    {
        var x = _fingers.Values.Average(f => f.Last.X);
        var y = _fingers.Values.Average(f => f.Last.Y);
        return new Point(x, y);
    }

    private double Distance()
    {
        var points = _fingers.Values.Select(f => f.Last).Take(2).ToList();
        return points.Count < 2 ? 0 : Length(points[0] - points[1]);
    }

    private static double Length(Vector v)
    {
        return Math.Sqrt(v.X * v.X + v.Y * v.Y);
    }

    private readonly record struct Finger(Point Start, Point Last, long Time);
}

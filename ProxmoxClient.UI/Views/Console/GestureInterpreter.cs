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
///         <item>탭 N번 = 왼쪽 클릭 N번(1 = 클릭, 2 = 더블 클릭) — 다음 터치를 가려내려고 마지막 탭 뒤
///             <see cref="MultiTapMs" /> 기다렸다 보낸다 (마우스 모드: 커서 위치 · 터치 모드: 첫 탭 자리)</item>
///         <item>탭 뒤 이어진 터치를 끌면 = 왼쪽 버튼 끌기, 그 자리에서 가만히 길게 누르면 = 오른쪽 버튼 누름(끌기·클릭).
///             첫 탭은 '이어서 누르겠다'는 신호라 빼고 그 사이 탭만큼 먼저 클릭한다: 탭 → 끌기 = 끌기,
///             탭 2번 → 끌기 = 클릭 후 끌기, 탭 3번 → 끌기 = 더블 클릭 후 끌기(사용자 요청)</item>
///         <item>두 손가락 탭 = 오른쪽 클릭, 세 손가락 탭 = 가운데 클릭</item>
///         <item>길게 누르기 = 오른쪽 클릭, 길게 누른 뒤 끌기 = 왼쪽 버튼 끌기(선택·창 이동)</item>
///         <item>한 손가락 끌기 = 커서 이동(마우스 모드) / 왼쪽 버튼 끌기(터치 모드)</item>
///         <item>두 손가락 끌기 = 스크롤(세로·가로), 확대한 상태에서는 화면 이동 · 벌리기·오므리기 = 확대·축소(+ 이동)</item>
///     </list>
/// </summary>
public sealed class GestureInterpreter(Viewport viewport, IRemotePointer remote)
{
    public const int LongPressMs = 450;
    public const double TapSlop = 12;
    public const int MultiTapMs = 350;
    public const double ScrollStep = 28;
    public const double PinchThreshold = 24;

    /// <summary>확대한 채 끌 때 손가락이 이 거리(보기 px)만큼 가장자리에 가까우면 그쪽으로 조금씩 화면을 옮긴다.</summary>
    public const double EdgeZone = 48;

    /// <summary>가장자리 자동 이동 최고 속도(보기 px/ms) — 손가락이 가장자리에 닿았을 때. 안쪽일수록 느리다.</summary>
    public const double EdgeSpeed = 0.25;

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
    /// <summary>지금 손가락이 탭에 이어진 터치 — 바로 떼면 탭 하나 더, 끌거나 길게 누르면 버튼 누름.</summary>
    private bool _doubleTapDrag;
    private long _lastTapUp = long.MinValue / 2;
    private Point _lastTapAt;

    /// <summary>아직 클릭으로 보내지 않은 이어진 탭 수(0 = 기다리는 클릭 없음).</summary>
    private int _tapCount;

    private Point _pendingClickAt;
    private long _lastEdgeTick;

    private enum Gesture
    {
        None,
        Pending,
        CursorMove,
        Drag,
        Scroll,
        Pan,
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
            _speed = _speedDistance = _speedTime = 0; // 새 손짓 — 지난 속도를 이어 쓰지 않는다
            _gesture = Gesture.Pending;
            _gestureStart = ms;
            _longPressFired = false;
            _lastEdgeTick = ms;
            // 방금 탭한 자리 근처를 곧바로 다시 누르면 이어진 터치 — 바로 떼면 탭이 하나 늘고, 끌거나 길게 누르면
            // 그 순간 버튼을 누른다(PressAfterTaps). 다른 곳을 누르면 기다리던 클릭을 먼저 보낸다
            var near = _tapCount > 0 && ms - _lastTapUp <= MultiTapMs && Length(view - _lastTapAt) < 48;
            _doubleTapDrag = near;
            if (!near) ResolvePendingClick();
            return;
        }

        // 두 번째 손가락: 한 손가락 동작(기다리던 클릭·눌린 버튼 포함)을 거두고 두 손가락 제스처로
        if (_doubleTapDrag)
        {
            _doubleTapDrag = false;
            ResolvePendingClick();
        }

        if (_buttons != 0) Release();
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
            case Gesture.Pending when moved && _doubleTapDrag:
                // 이어진 터치를 누른 채 끌기 = 왼쪽 버튼 끌기(앞선 탭이 더 있으면 그만큼 클릭한 뒤).
                // 가만히 길게 누른 뒤라면 오른쪽 버튼 — 보통은 Tick 이 먼저 잡는다
                PressAfterTaps(_longPressFired || ms - _gestureStart >= LongPressMs ? Right : Left);
                // 버튼은 첫 탭 자리에서 내려갔다 — 문턱 전에는 커서를 움직이지 않고, 문턱을 넘을 때 모인 만큼을
                // 가속 없이(손가락 거리 그대로) 따라잡는다
                if (Mode == TouchInputMode.Touch) Point(view, _buttons);
                else MoveCursor(view - finger.Start, ms - _gestureStart, accelerate: false);
                break;
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
                    // 문턱을 넘었다 — 이번 이벤트만큼만 움직인다(문턱 전 움직임은 이미 따라갔다). 모아 둔 12px 를
                    // 마지막 8ms 로 나눠 '빠른 손짓'으로 보고 가속해 커서가 툭 튀던 문제(실기기 보고)를 막는다
                    _gesture = Gesture.CursorMove;
                    MoveCursor(delta, ms - finger.Time);
                }

                break;
            case Gesture.Pending when Mode == TouchInputMode.Mouse && !_longPressFired && !_doubleTapDrag:
                // 문턱 전에도 커서가 손가락을 바로 따라간다(가속 없이) — 멈춰 있다가 문턱에서 한꺼번에 움직이지 않게.
                // 탭은 문턱 안이라 커서가 거의 움직이지 않는다
                MoveCursor(delta, ms - finger.Time, accelerate: false);
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
            case Gesture.Pending when _doubleTapDrag:
                _doubleTapDrag = false;
                if (duration < LongPressMs)
                {
                    // 이어진 터치를 바로 뗐다 — 탭이 하나 늘었다(2번 = 더블 클릭, 3번 = 세 번 클릭). 다음 터치를 기다린다
                    _tapCount++;
                    _lastTapUp = ms;
                    _lastTapAt = finger.Start;
                }
                else
                {
                    // 가만히 길게 눌렀다 뗐는데 Tick 이 못 잡았다 — 오른쪽 버튼 누름·놓음
                    PressAfterTaps(Right);
                    Release();
                }

                break;
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
                Tap(finger, ms);
                break;
            case Gesture.Drag:
                Release();
                break;
            case Gesture.Pinch or Gesture.Pan:
                KeepCursorInView(); // 확대·이동으로 커서가 가장자리 밖에 남지 않게
                break;
        }

        _gesture = Gesture.None;
        _maxFingers = 0;
    }

    /// <summary>
    ///     주기적으로 불러 길게 누르기를 손을 떼기 전에 알아챈다 — <see cref="LongPressArmed" /> 가 켜지면 화면이
    ///     "떼면 오른쪽 클릭 · 끌면 선택" 표시를 보여 준다. 클릭은 손을 뗄 때 보낸다(끌기와 구분하려고).
    ///     기다리던 탭 클릭도 다음 터치가 오지 않으면 여기서 보낸다.
    /// </summary>
    public bool Tick(long ms)
    {
        // 탭 뒤 다음 터치를 기다리다 시간이 지났다 — 모인 탭 수만큼 클릭(1 = 클릭, 2 = 더블 클릭)
        if (_tapCount > 0 && _fingers.Count == 0 && ms - _lastTapUp > MultiTapMs)
        {
            ResolvePendingClick();
            return true;
        }

        if (EdgeScroll(ms)) return true;

        if (_gesture != Gesture.Pending || _fingers.Count != 1 || _longPressFired || _maxFingers != 1) return false;

        var finger = _fingers.Values.First();
        if (ms - _gestureStart < LongPressMs || Length(finger.Last - finger.Start) > TapSlop) return false;

        if (_doubleTapDrag)
        {
            // 이어진 터치를 그 자리에서 가만히 길게 눌렀다 — 오른쪽 버튼을 누른 상태(끌면 오른쪽 끌기, 떼면 오른쪽 클릭)
            PressAfterTaps(Right);
            return true;
        }

        _longPressFired = true;
        return true;
    }

    /// <summary>손가락이 하나라도 닿아 있다.</summary>
    public bool HasFingers => _fingers.Count > 0;

    /// <summary>손가락이 닿아 있거나 클릭을 기다리는 중 — 화면이 <see cref="Tick" /> 을 계속 불러야 한다.</summary>
    public bool NeedsTicks => _fingers.Count > 0 || _tapCount > 0;

    /// <summary>확대한 채 한 손가락으로 끄는 중(버튼 누름) — 가장자리 이동이 일어날 수 있어 타이머를 빠르게 돌린다.</summary>
    public bool IsEdgeDragging => _gesture == Gesture.Drag && _buttons != 0 && _fingers.Count == 1 && viewport.IsZoomed;

    /// <summary>
    ///     한 손가락 첫 탭 — 클릭을 바로 보내지 않고 <see cref="MultiTapMs" /> 동안 다음 터치를 기다린다.
    ///     오지 않으면 <see cref="Tick" /> 이 클릭, 같은 자리 근처에 오면 이어진 터치로 받는다(<see cref="Down" />).
    /// </summary>
    private void Tap(Finger finger, long ms)
    {
        _lastTapUp = ms;
        _lastTapAt = finger.Start;
        var at = TapPoint(finger);
        _tapCount = 1;
        _pendingClickAt = at;
        if (Mode == TouchInputMode.Touch)
        {
            Cursor = at;
            Send(_buttons, at); // 클릭을 기다리는 동안 커서는 먼저 누른 곳으로
        }
    }

    /// <summary>기다리던 탭을 탭 수만큼 클릭으로 보낸다(첫 탭 자리에서 — 원격이 더블 클릭으로 알아보게).</summary>
    private void ResolvePendingClick()
    {
        if (_tapCount == 0) return;

        var count = _tapCount;
        _tapCount = 0;
        Clicks(_pendingClickAt, count);
    }

    /// <summary>
    ///     이어진 터치가 끌기·길게 누르기가 됐다 — 첫 탭은 '이어서 누르겠다'는 신호라 빼고, 그 사이 탭만큼 클릭한 뒤
    ///     버튼을 누른 상태로. 탭 → 누름 = 누름, 탭 2번 → 누름 = 클릭 후 누름, 탭 3번 → 누름 = 더블 클릭 후 누름.
    /// </summary>
    private void PressAfterTaps(int button)
    {
        var clicks = _tapCount - 1;
        _tapCount = 0;
        _doubleTapDrag = false;
        if (clicks > 0) Clicks(_pendingClickAt, clicks);
        Cursor = _pendingClickAt;
        Press(button);
        _gesture = Gesture.Drag;
    }

    /// <summary>길게 누른 상태(아직 손을 떼지 않음).</summary>
    public bool LongPressArmed => _longPressFired && _gesture == Gesture.Pending && _fingers.Count == 1;

    /// <summary>제스처를 강제로 끝낸다(화면을 떠날 때 버튼이 눌린 채 남지 않게).</summary>
    public void Cancel()
    {
        if (_buttons != 0) Release();
        _tapCount = 0;
        _doubleTapDrag = false;
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
            else if (Length(centroid - _lastCentroid) > TapSlop && _fingers.Count == 2)
                _gesture = viewport.IsZoomed ? Gesture.Pan : Gesture.Scroll; // 확대 중이면 화면 이동
            else return;
        }

        switch (_gesture)
        {
            case Gesture.Pinch:
                if (_lastDistance > 0) viewport.ZoomAt(distance / _lastDistance, centroid);
                viewport.PanBy(centroid - _lastCentroid);
                break;
            case Gesture.Pan:
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

    private double _speedDistance;
    private double _speedTime;

    /// <summary>손가락 속도(px/ms) — 이벤트 사이 시간으로 부드럽게 한 값(가속 배율에 쓴다).</summary>
    private double _speed;

    /// <summary>속도를 부드럽게 하는 시간 상수 — 터치 점이 묶여 와 한 이벤트의 시간 간격이 0·16ms 로 튀어도 가속이 고르게.</summary>
    private const double SpeedSmoothingMs = 60;

    private void MoveCursor(Vector viewDelta, long elapsedMs, bool accelerate = true)
    {
        // 빠르게 움직일수록 더 멀리(가속) — 작은 화면에서도 큰 원격 화면을 끝까지 옮길 수 있게.
        // 이벤트마다 속도를 새로 재면 Android 가 묶어 보내는 터치 점(시각 차 0ms·16ms)에서 배율이 번갈아 튀어
        // 커서가 툭툭 끊겨 움직인다 — 거리·시간을 시간 상수로 누적해 속도를 부드럽게 한다.
        var dt = Math.Max(0, elapsedMs);
        var weight = Math.Exp(-dt / SpeedSmoothingMs);
        _speedDistance = _speedDistance * weight + Length(viewDelta);
        _speedTime = _speedTime * weight + dt;
        _speed = _speedTime > 0.5 ? _speedDistance / _speedTime : _speed;
        // 부드러운 가속 — 느리면 1:1, 0.4px/ms 넘으면 조금씩 늘어 최대 2.5배(계단 없이 이어지게)
        var accel = accelerate ? Math.Clamp(1 + (_speed - 0.4) * 1.0, 1, 2.5) : 1;
        var remoteDelta = viewDelta / viewport.Scale * CursorSpeed * accel;
        var next = Cursor + remoteDelta;
        Cursor = new Point(Math.Clamp(next.X, 0, Math.Max(0, viewport.Remote.Width - 1)),
            Math.Clamp(next.Y, 0, Math.Max(0, viewport.Remote.Height - 1)));
        viewport.EnsureVisible(Cursor, CursorMargin());
        Send(_buttons, Cursor);
    }

    /// <summary>
    ///     확대 중 커서가 보기 가장자리에 붙지 않게 두는 여유(보기 px) — 짧은 변의 20%(40~160). 커서가 이 안쪽에 있어야
    ///     주변이 보이고, 더 밀면 화면이 먼저 따라 움직인다(사용자 요청).
    /// </summary>
    private double CursorMargin()
    {
        if (!viewport.IsZoomed) return 40;

        return Math.Clamp(Math.Min(viewport.View.Width, viewport.View.Height) * 0.2, 40, 160);
    }

    /// <summary>
    ///     마우스 모드: 확대·이동·화면 모드 변경 뒤 커서가 여유 밖(가장자리·화면 밖)에 있으면 안쪽으로 옮긴다.
    ///     화면은 그대로 두고 커서만 옮긴다(보던 곳이 튀지 않게).
    /// </summary>
    public void KeepCursorInView()
    {
        if (Mode != TouchInputMode.Mouse || !viewport.IsZoomed || _buttons != 0) return;

        var margin = CursorMargin();
        var p = viewport.ToView(Cursor);
        var x = Math.Clamp(p.X, Math.Min(margin, viewport.View.Width / 2), Math.Max(viewport.View.Width - margin, viewport.View.Width / 2));
        var y = Math.Clamp(p.Y, Math.Min(margin, viewport.View.Height / 2), Math.Max(viewport.View.Height - margin, viewport.View.Height / 2));
        if (Math.Abs(x - p.X) < 0.5 && Math.Abs(y - p.Y) < 0.5) return;

        Cursor = viewport.ToRemote(new Point(x, y));
        Send(0, Cursor);
    }

    /// <summary>
    ///     확대한 채 끄는 중(버튼 누름) 손가락이 가장자리 <see cref="EdgeZone" /> 안에 머물면 그쪽으로 조금씩 옮긴다 —
    ///     손가락이 화면 끝에 막혀도 계속 끌 수 있게(사용자 요청). 터치 모드: 화면을 옮기고 손가락 아래 자리로 끌기를 잇는다.
    ///     마우스 모드: 커서를 그쪽으로 옮기고 화면은 커서를 따라간다.
    /// </summary>
    private bool EdgeScroll(long ms)
    {
        var elapsed = Math.Clamp(ms - _lastEdgeTick, 0, 100);
        _lastEdgeTick = ms;
        if (_gesture != Gesture.Drag || _buttons == 0 || _fingers.Count != 1 || !viewport.IsZoomed) return false;

        var at = _fingers.Values.First().Last;
        var zone = Math.Min(EdgeZone, Math.Min(viewport.View.Width, viewport.View.Height) / 4);
        if (zone <= 0) return false;

        double Push(double pos, double size) =>
            pos < zone ? -Math.Min(1, (zone - pos) / zone) : pos > size - zone ? Math.Min(1, (pos - (size - zone)) / zone) : 0;

        var direction = new Vector(Push(at.X, viewport.View.Width), Push(at.Y, viewport.View.Height));
        if (direction == default || elapsed == 0) return false;

        var step = direction * EdgeSpeed * elapsed;
        if (Mode == TouchInputMode.Touch)
        {
            var before = viewport.Offset;
            viewport.PanBy(-step); // 오른쪽 가장자리 = 오른쪽 내용이 보이도록 화면을 왼쪽으로
            if (viewport.Offset == before) return false; // 원격 화면 끝 — 더 갈 곳이 없다

            Point(at, _buttons);
            return true;
        }

        var cursor = Cursor;
        MoveCursor(step, elapsed, accelerate: false);
        return Cursor != cursor;
    }

    private void Point(Point view, int buttons)
    {
        Cursor = viewport.ToRemote(view);
        Send(buttons, Cursor);
    }

    private void Clicks(Point remotePoint, int count)
    {
        Cursor = remotePoint;
        Send(0, remotePoint);
        for (var i = 0; i < count; i++)
        {
            Send(Left, remotePoint);
            Send(0, remotePoint);
        }
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

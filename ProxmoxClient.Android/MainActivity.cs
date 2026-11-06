using Android.App;
using Android.Content.PM;
using Android.Views;
using AndroidX.Activity;
using Avalonia.Android;
using ProxmoxClient.UI.Platform;

namespace ProxmoxClient.Droid;

/// <remarks>
///     ConfigurationChanges — 이 설정이 바뀌어도 액티비티를 다시 만들지 않는다(화면 회전, 분할 화면·창 크기,
///     다크 모드, 하드웨어 키보드·마우스 연결, 삼성 DeX 전환, 글꼴 크기·화면 배율·언어 변경).
///     다시 만들어지더라도 셸(페이지 스택·연결된 터미널)은 App 이 같은 것을 다시 붙인다.
///     휴대폰/태블릿 배치(<see cref="PlatformInfo.FormFactor" />)는 앱을 시작할 때 한 번 정한다 — 이미 연 화면은
///     그 배치로 만들어져 있어서 실행 중에 바꾸지 않는다(DeX·접는 폰에서 바꾸려면 앱을 다시 시작).
///     표시 언어도 시작할 때 정해지므로 시스템 언어를 바꾸면 다음 실행부터 바뀐다.
/// </remarks>
[Activity(
    Label = "Proxmox Client",
    Theme = "@style/AppTheme",
    Icon = "@mipmap/appicon",
    MainLauncher = true,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize |
                           ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation |
                           ConfigChanges.Density | ConfigChanges.FontScale | ConfigChanges.Locale |
                           ConfigChanges.LayoutDirection)]
public class MainActivity : AvaloniaMainActivity
{
    /// <summary>마우스 오른쪽 버튼 뒤 이 시간 안에 오는 '뒤로 가기'는 오른쪽 클릭이 바뀐 것으로 보고 버린다.</summary>
    private const long RightClickBackWindowMs = 400;

    private long _lastSecondaryButtonAt = long.MinValue / 2;
    private RightClickBackFilter? _backFilter;

    /// <summary>지금 화면의 액티비티 — 권한 요청(알림)처럼 액티비티가 필요한 곳에서 쓴다.</summary>
    internal static MainActivity? Current { get; private set; }

    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);
        ApplyScreenSecurity(ScreenSecurity.Enabled);
#if DEBUG
        DebugTestHook.Log($"activity created recreated={savedInstanceState is not null}");
#endif
    }

    protected override void OnStart()
    {
        base.OnStart();
        // Avalonia 가 OnStart 에서 뒤로 가기 콜백을 등록한다 — 그 뒤에 등록해야 먼저 불린다(나중 것이 우선)
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            _backFilter = new RightClickBackFilter(this);
            OnBackPressedDispatcher.AddCallback(this, _backFilter);
        }
    }

    protected override void OnStop()
    {
        _backFilter?.Remove();
        _backFilter = null;
        base.OnStop();
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
#if DEBUG
        DebugTestHook.Log("activity destroyed");
#endif
        base.OnDestroy();
    }

    /// <summary>화면 보호(FLAG_SECURE) — 최근 앱 미리보기를 가리고 캡처·녹화를 막는다.</summary>
    internal void ApplyScreenSecurity(bool enabled)
    {
        if (enabled) Window?.AddFlags(WindowManagerFlags.Secure);
        else Window?.ClearFlags(WindowManagerFlags.Secure);
        // Avalonia 는 SurfaceView 에 그린다 — 그 층에도 같은 값을 건다(다음 그리기 때 층에 반영된다)
        var count = Window?.DecorView is { } decor ? SetSurfaceViewsSecure(decor, enabled) : 0;
#if DEBUG
        DebugTestHook.Log($"screen security {enabled} surfaceViews={count}");
#endif
    }

    private static int SetSurfaceViewsSecure(View view, bool enabled)
    {
        var count = 0;
        if (view is SurfaceView surface)
        {
            surface.SetSecure(enabled);
            surface.Invalidate();
            count++;
        }

        if (view is ViewGroup group)
            for (var i = 0; i < group.ChildCount; i++)
                if (group.GetChildAt(i) is { } child)
                    count += SetSurfaceViewsSecure(child, enabled);
        return count;
    }

    /// <summary>
    ///     태블릿에 연결한 마우스의 오른쪽 버튼을 Android 가 '뒤로 가기'로 바꿔 보내는 것을 막는다 —
    ///     콘솔에서 오른쪽 클릭하면 화면이 닫히지 않고 게스트에 오른쪽 클릭이 가야 한다(화면 뒤로 가기는 제스처·버튼으로).
    ///     Android 13 이상(예측 뒤로 가기)은 뒤로 가기 키가 이 메서드에 오기 전에 뒤로 가기 콜백으로 가므로
    ///     <see cref="RightClickBackFilter" /> 가 같은 일을 한다.
    /// </summary>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is { KeyCode: Keycode.Back } && IsMouse(e.Source))
        {
#if DEBUG
            DebugTestHook.Log($"mouse BACK key dropped action={e.Action}");
#endif
            MarkSecondaryButton();
            return true;
        }

        // 뒤로 가기 키(3버튼·제스처 뒤로 가기 — 예측 뒤로 가기를 켜지 않은 앱에는 키로 온다)는 화면에 보내지 않고
        // 바로 뒤로 가기 처리로 넘긴다. 화면에 보내면 Avalonia 가 Esc 키로 바꿔 포커스를 가진 터미널 입력 칸이 먹어 버려
        // (터미널에 ESC 가 가고) 터미널 화면이 닫히지 않았다(에뮬레이터 검사로 확인)
        if (e is { KeyCode: Keycode.Back })
        {
            if (e.Action == KeyEventActions.Up && !e.IsCanceled)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(33)) OnBackPressedDispatcher.OnBackPressed(); // 오른쪽 클릭 걸러 내기 포함
                else OnBackPressed();
            }

            return true;
        }

        return base.DispatchKeyEvent(e);
    }

    public override bool DispatchTouchEvent(MotionEvent? e)
    {
        TrackSecondaryButton(e);
        return base.DispatchTouchEvent(e);
    }

    public override bool DispatchGenericMotionEvent(MotionEvent? e)
    {
        TrackSecondaryButton(e);
        return base.DispatchGenericMotionEvent(e);
    }

    /// <summary>Android 12 이하 — 뒤로 가기가 여기로 온다.</summary>
    public override void OnBackPressed()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) && IsRightClickBack()) return;

#pragma warning disable CA1422
        base.OnBackPressed();
#pragma warning restore CA1422
    }

    /// <summary>마우스 오른쪽 버튼을 누르거나 뗀 때를 기억한다(그 직후의 '뒤로 가기'는 버린다).</summary>
    private void TrackSecondaryButton(MotionEvent? e)
    {
        if (e is null || !IsMouse(e.Source)) return;

        var secondary = (e.ButtonState & MotionEventButtonState.Secondary) != 0 ||
                        (e.ActionMasked is MotionEventActions.ButtonPress or MotionEventActions.ButtonRelease &&
                         e.ActionButton == MotionEventButtonState.Secondary);
        if (secondary) MarkSecondaryButton();
#if DEBUG
        if (secondary) DebugTestHook.Log($"secondary button {e.ActionMasked}");
#endif
    }

    private void MarkSecondaryButton()
    {
        _lastSecondaryButtonAt = global::Android.OS.SystemClock.UptimeMillis();
    }

    private bool IsRightClickBack()
    {
        return global::Android.OS.SystemClock.UptimeMillis() - _lastSecondaryButtonAt <= RightClickBackWindowMs;
    }

    private static bool IsMouse(InputSourceType source)
    {
        return (source & InputSourceType.Mouse) == InputSourceType.Mouse;
    }

    /// <summary>
    ///     Avalonia 의 뒤로 가기 콜백보다 먼저 불리는 콜백 — 오른쪽 클릭 직후의 뒤로 가기는 버리고,
    ///     아니면 잠깐 꺼서 다음 콜백(Avalonia: 대화상자 닫기·페이지 뒤로·앱 나가기)으로 넘긴다.
    /// </summary>
    private sealed class RightClickBackFilter(MainActivity activity) : OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed()
        {
#if DEBUG
            DebugTestHook.Log($"back callback rightClick={activity.IsRightClickBack()}");
#endif
            if (activity.IsRightClickBack()) return;

            Enabled = false;
            try
            {
                activity.OnBackPressedDispatcher.OnBackPressed();
            }
            finally
            {
                Enabled = true;
            }
        }
    }
}

/// <summary>설정의 "화면 보호"를 지금 액티비티 창에 건다(새 액티비티는 OnCreate 에서 다시 건다).</summary>
internal sealed class AndroidScreenSecurityHost : IScreenSecurityHost
{
    public void Apply(bool enabled)
    {
        MainActivity.Current?.ApplyScreenSecurity(enabled);
    }
}

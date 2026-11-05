using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace ProxmoxClient.Droid;

[Activity(
    Label = "Proxmox Client",
    Theme = "@style/AppTheme",
    Icon = "@mipmap/appicon",
    MainLauncher = true,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public class MainActivity : AvaloniaMainActivity
{
    /// <summary>
    ///     태블릿에 연결한 마우스의 오른쪽 버튼을 Android 가 '뒤로 가기' 키로 바꿔 보내는 것을 막는다 —
    ///     콘솔에서 오른쪽 클릭하면 화면이 닫히지 않고 게스트에 오른쪽 클릭이 가야 한다(화면 뒤로 가기는 제스처·버튼으로).
    /// </summary>
    public override bool DispatchKeyEvent(global::Android.Views.KeyEvent? e)
    {
        if (e is { KeyCode: global::Android.Views.Keycode.Back } &&
            (e.Source & global::Android.Views.InputSourceType.Mouse) == global::Android.Views.InputSourceType.Mouse)
            return true;

        return base.DispatchKeyEvent(e);
    }
}

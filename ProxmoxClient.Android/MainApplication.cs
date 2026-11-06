using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using ProxmoxClient.UI;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.Droid;

[Application]
public class MainApplication : AvaloniaAndroidApplication<App>
{
    protected MainApplication(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // 앱 기록(처리하지 못한 예외)은 앱 전용 폴더에 — 설정 > 일반 > 진단에서 보거나 저장한다
        ProxmoxClient.UI.Platform.AppLog.Directory = Path.Combine(FilesDir!.AbsolutePath, "logs");
        ProxmoxClient.UI.Platform.SessionKeepAlive.StatePath = Path.Combine(FilesDir!.AbsolutePath, "open-terminals.txt");
        ProxmoxClient.UI.Platform.AppLog.InstallGlobalHandlers();
        // Java 쪽으로 넘어간 .NET 예외(앱이 죽는다) — 기록만 하고 그대로 죽게 둔다
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
            ProxmoxClient.UI.Platform.AppLog.Error("Android (fatal)", e.Exception);
        // 비밀번호·토큰은 Android 키스토어 키로 암호화해 앱 전용 저장소에 둔다
        // 짧은 변이 600dp 이상(7인치 이상 태블릿)이면 PC 와 같은 배치, 아니면 휴대폰 배치
        ProxmoxClient.UI.Platform.PlatformInfo.FormFactor =
            Resources?.Configuration?.SmallestScreenWidthDp >= 600
                ? ProxmoxClient.UI.Platform.FormFactor.Tablet
                : ProxmoxClient.UI.Platform.FormFactor.Mobile;
        AppServices.SecretStore = new KeystoreSecretStore(this);
        // SPICE 콘솔 — .vv 파일을 SPICE 앱(aSPICE·Opaque 등)에 ACTION_VIEW 로 넘긴다(데스크톱의 remote-viewer 대신)
        ProxmoxClient.UI.Platform.SpiceLaunchers.Current = new AndroidSpiceLauncher(this);
        // 터미널 연결 유지 — 연결된 동안 포그라운드 서비스(알림)로 앱을 살려 두어 배경에서도 세션이 끊기지 않게
        ProxmoxClient.UI.Platform.SessionKeepAlive.Host = new AndroidSessionKeepAliveHost(this);
        // 설정의 "화면 보호" — 액티비티 창에 FLAG_SECURE
        ProxmoxClient.UI.Platform.ScreenSecurity.Host = new AndroidScreenSecurityHost();
        AppServices.ProfilesDirectory = Path.Combine(FilesDir!.AbsolutePath, "profiles");
        return base.CustomizeAppBuilder(builder)
            .UseAppFonts()
            .With(new AndroidPlatformOptions
            {
                // GPU 렌더링: Vulkan 우선(드라이버 부담·프레임 지연이 가장 적다).
                // Vulkan 을 못 쓰는 기기(구형·에뮬레이터 일부)는 OpenGL ES(EGL), 그래도 안 되면 소프트웨어로 넘어간다.
                RenderingMode = [AndroidRenderingMode.Vulkan, AndroidRenderingMode.Egl, AndroidRenderingMode.Software]
            });
    }
}

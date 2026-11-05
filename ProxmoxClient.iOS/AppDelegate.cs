using Avalonia;
using Avalonia.iOS;
using Foundation;
using ProxmoxClient.UI;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using UIKit;

namespace ProxmoxClient.iOS;

/// <summary>
///     iOS · iPadOS 시작 — Android 헤드(MainApplication)와 같은 일: 화면 형태, 비밀번호 저장소, 프로필 위치, 글꼴, 렌더링.
/// </summary>
[Register("AppDelegate")]
#pragma warning disable CA1711 // 'Delegate' 접미사 — iOS 관례 이름
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // iPad 는 PC 와 같은 배치(Android 태블릿과 같다), iPhone 은 휴대폰 배치
        PlatformInfo.FormFactor = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad
            ? FormFactor.Tablet
            : FormFactor.Mobile;
        // 비밀번호·토큰은 iOS 키체인(이 기기에서만, 처음 잠금 해제 뒤 읽기)
        AppServices.SecretStore = new IosKeychainSecretStore();
        AppServices.ProfilesDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "profiles");

        return base.CustomizeAppBuilder(builder)
            .UseAppFonts()
            .With(new iOSPlatformOptions
            {
                // GPU 렌더링: Metal 우선(iOS 의 기본 GPU API — 지연이 가장 적다), 안 되면 OpenGL ES
                RenderingMode = [iOSRenderingMode.Metal, iOSRenderingMode.OpenGl]
            });
    }
}

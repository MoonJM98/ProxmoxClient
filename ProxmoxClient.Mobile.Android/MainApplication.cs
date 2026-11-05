using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using ProxmoxClient.Mobile.Services;

namespace ProxmoxClient.Mobile.Android;

[Application]
public class MainApplication : AvaloniaAndroidApplication<App>
{
    protected MainApplication(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // 비밀번호·토큰은 Android 키스토어 키로 암호화해 앱 전용 저장소에 둔다
        AppServices.SecretStore = new KeystoreSecretStore(this);
        AppServices.ProfilesDirectory = Path.Combine(FilesDir!.AbsolutePath, "profiles");
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}

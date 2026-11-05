using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace ProxmoxClient.Mobile.Android;

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
}

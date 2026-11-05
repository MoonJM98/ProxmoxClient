using UIKit;

namespace ProxmoxClient.iOS;

public static class Application
{
    /// <summary>앱 시작점 — 앱 대리자(<see cref="AppDelegate" />)가 Avalonia 를 띄운다.</summary>
    private static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}

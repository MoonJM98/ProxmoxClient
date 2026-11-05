using Avalonia;
using ProxmoxClient.UI;

namespace ProxmoxClient.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>디자이너도 이 메서드를 쓴다.</summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        // 플랫폼(Win32 · X11/Wayland · macOS)은 실행 중인 OS 에서 자동으로 고른다
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}

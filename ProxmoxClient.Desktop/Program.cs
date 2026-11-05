using Avalonia;
using ProxmoxClient.UI;

namespace ProxmoxClient.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ProxmoxClient.UI.Platform.PlatformInfo.FormFactor = ProxmoxClient.UI.Platform.FormFactor.Desktop;
        if (Environment.GetEnvironmentVariable("PROXMOXCLIENT_LOG") == "1")
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>디자이너도 이 메서드를 쓴다.</summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        // 플랫폼(Win32 · X11/Wayland · macOS)은 실행 중인 OS 에서 자동으로 고른다.
        // 렌더링은 OS 마다 지연이 가장 적은 GPU 경로를 먼저 시도하고, 안 되면 다음 것으로 넘어간다.
        // PROXMOXCLIENT_RENDER=software — GPU 드라이버가 초기화는 되는데 화면이 검게 나오는 PC 용 비상 스위치(CPU 로 그린다).
        var software = string.Equals(Environment.GetEnvironmentVariable("PROXMOXCLIENT_RENDER"), "software", StringComparison.OrdinalIgnoreCase);
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                // Direct3D(ANGLE) + 저지연 DXGI 스왑 체인 — 입력에서 화면까지 지연이 가장 짧다(Feature Level 11_3 필요).
                RenderingMode = software
                    ? [Win32RenderingMode.Software]
                    : [Win32RenderingMode.AngleEgl, Win32RenderingMode.Vulkan, Win32RenderingMode.Software],
                CompositionMode = software
                    ? [Win32CompositionMode.RedirectionSurface]
                    :
                    [
                        Win32CompositionMode.LowLatencyDxgiSwapChain, Win32CompositionMode.WinUIComposition,
                        Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface
                    ]
            })
            .With(new X11PlatformOptions
            {
                RenderingMode = software
                    ? [X11RenderingMode.Software]
                    : [X11RenderingMode.Vulkan, X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software]
            })
            .With(new AvaloniaNativePlatformOptions
            {
                // macOS: Metal
                RenderingMode = software
                    ? [AvaloniaNativeRenderingMode.Software]
                    : [AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software]
            })
            .UseAppFonts()
            // PROXMOXCLIENT_LOG=1 — 플랫폼·렌더링 초기화 기록을 표준 오류로(어느 GPU 경로가 왜 안 됐는지 확인)
            .LogToTrace(Environment.GetEnvironmentVariable("PROXMOXCLIENT_LOG") == "1"
                ? Avalonia.Logging.LogEventLevel.Debug
                : Avalonia.Logging.LogEventLevel.Warning);
    }
}

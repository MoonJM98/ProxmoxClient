using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ProxmoxClient.App;

/// <summary>창 제목표시줄을 Windows 다크 모드로 변경(DWMWA_USE_IMMERSIVE_DARK_MODE).</summary>
public static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ApplyDarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var on = 1;
            _ = DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)); // Win10 2004+
            _ = DwmSetWindowAttribute(handle, 19, ref on, sizeof(int)); // Win10 1809 폴백
        };
    }
}
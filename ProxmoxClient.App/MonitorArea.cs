using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ProxmoxClient.App;

/// <summary>창이 놓인 모니터의 작업 영역(작업 표시줄 제외, WPF 단위) — SystemParameters.WorkArea 는 주 모니터뿐이다.</summary>
internal static class MonitorArea
{
    private const uint MonitorDefaultToNearest = 2;

    public static Rect WorkAreaOf(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (handle == IntPtr.Zero || monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return SystemParameters.WorkArea;

        // 장치 픽셀 → WPF 단위(모니터 배율)
        var toDip = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice;
        var work = new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left,
            info.Work.Bottom - info.Work.Top);
        return toDip is { } m ? Rect.Transform(work, m) : work;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}

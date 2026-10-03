using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ProxmoxClient.App;

/// <summary>
///     콘솔 창에 딸린 창(파일·설정)을 콘솔을 가리지 않게 옆에 둔다 — 같은 모니터에서 콘솔 오른쪽, 자리가 없으면 왼쪽,
///     그것도 없으면 콘솔 위 가운데. 콘솔이 최대화돼 있으면 그 모니터 가운데.
/// </summary>
internal static class BesideOwner
{
    private const double Gap = 8;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>
    ///     보이기 전에 부른다. matchHeight 면 높이를 콘솔 창에 맞춘다(파일 창), 아니면 창 높이(내용 크기)를 그대로 두고
    ///     처음 배치된 뒤 화면 아래로 넘치면 올린다(설정 창).
    /// </summary>
    public static void Place(Window window, Window owner, bool matchHeight)
    {
        PlaceCore(window, owner, matchHeight);
        window.SourceInitialized += (_, _) => MatchOwnerDpi(window, owner, matchHeight);
        if (!matchHeight) window.Loaded += (_, _) => KeepOnScreen(window);
    }

    private static void PlaceCore(Window window, Window owner, bool matchHeight)
    {
        var area = MonitorArea.WorkAreaOf(owner);
        var normal = owner.WindowState == WindowState.Normal && owner.ActualHeight > 0;
        var bounds = normal ? new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight) : area;
        if (matchHeight)
            window.Height = Math.Min(Math.Max(window.MinHeight, normal ? bounds.Height : window.Height), area.Height);
        // 내용 크기 창은 아직 높이를 모른다 — 최소 높이로 어림하고 배치된 뒤 KeepOnScreen 이 맞춘다
        var height = double.IsNaN(window.Height) ? window.MinHeight : window.Height;
        if (!normal) bounds = new Rect(area.Left, area.Top + (area.Height - height) / 2, area.Width, height);
        window.Top = Math.Clamp(bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - height));

        var right = bounds.Right + Gap;
        var left = bounds.Left - window.Width - Gap;
        if (right + window.Width <= area.Right)
            window.Left = right;
        else if (left >= area.Left)
            window.Left = left;
        else
            window.Left = Math.Clamp(bounds.Left + (bounds.Width - window.Width) / 2, area.Left,
                Math.Max(area.Left, area.Right - window.Width));
    }

    /// <summary>
    ///     창 핸들이 생긴 뒤(아직 보이기 전) — 배율이 다른 모니터에 있는 콘솔 창 옆이면 자리를 다시 잡는다.
    ///     PerMonitorV2 에서 WPF 의 Left/Top 은 그 창이 놓인 모니터 배율로 나눈 값이라, 콘솔 창(150% 보조 모니터 등)의
    ///     Left/Top 을 그대로 쓰면 이 창은 자기 DPI(주 모니터 등)로 바꿔 엉뚱한 모니터에 놓인다.
    ///     먼저 콘솔 창 자리로 옮겨 DPI 를 맞춘 뒤(옮기는 동안 WPF 가 크기를 새 배율로 맞춘다) 같은 기준으로 다시 놓는다.
    /// </summary>
    private static void MatchOwnerDpi(Window window, Window owner, bool matchHeight)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        if (handle == IntPtr.Zero || ownerHandle == IntPtr.Zero || GetDpiForWindow(handle) == GetDpiForWindow(ownerHandle)
            || !GetWindowRect(ownerHandle, out var ownerRect))
            return;

        SetWindowPos(handle, IntPtr.Zero, ownerRect.Left, ownerRect.Top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        PlaceCore(window, owner, matchHeight);
    }

    /// <summary>내용 크기 창이 배치된 뒤 — 작업 영역 아래로 넘치면 올린다.</summary>
    private static void KeepOnScreen(Window window)
    {
        var area = MonitorArea.WorkAreaOf(window);
        if (window.Top + window.ActualHeight > area.Bottom)
            window.Top = Math.Max(area.Top, area.Bottom - window.ActualHeight);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height,
        uint flags);
}

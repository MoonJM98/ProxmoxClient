using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ProxmoxClient.App.Rendering;

/// <summary>
///     GPU 로 콘솔 화면을 그릴 자식 창(<see cref="DirectScreenRenderer" />)을 WPF 에 끼운다.
///     창은 비활성(WS_DISABLED) — Windows 는 비활성 자식 창의 마우스 입력을 부모로 보내므로 클릭·이동·휠·끌어다 놓기와
///     커서 모양은 그 아래 WPF 요소(콘솔 화면 Image)가 그대로 받는다. 이 요소는 WPF 적중 검사에서도 빠진다.
///     WPF 요소는 이 창 위에 그려지지 않으므로(에어스페이스) 위에 덮을 것이 있으면 숨겨야 한다.
/// </summary>
internal sealed class DirectScreenHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsDisabled = 0x08000000;
    private const int WsClipSiblings = 0x04000000;
    private const int SsBlackRect = 0x04;
    private const int WsExNoActivate = 0x08000000;

    public DirectScreenHost()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>창을 만들면서 시작한 렌더러(창이 없으면 null).</summary>
    public DirectScreenRenderer? Renderer { get; private set; }

    /// <summary>렌더러가 준비됐다(창을 만든 직후, UI 스레드).</summary>
    public event Action<DirectScreenRenderer>? RendererCreated;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var hwnd = CreateWindowEx(WsExNoActivate, "Static", string.Empty,
            WsChild | WsVisible | WsDisabled | WsClipSiblings | SsBlackRect, 0, 0, 1, 1, hwndParent.Handle,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx: {Marshal.GetLastWin32Error()}");

        var renderer = new DirectScreenRenderer(hwnd, 1, 1);
        try
        {
            Renderer = renderer;
            RendererCreated?.Invoke(renderer); // 실패 알림(Failed)을 구독한 뒤에 시작한다
            renderer.Start();
        }
        catch
        {
            Renderer = null;
            renderer.Dispose();
            DestroyWindow(hwnd);
            throw;
        }

        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Renderer?.Dispose(); // 스왑체인을 먼저 놓고 창을 지운다
        Renderer = null;
        DestroyWindow(hwnd.Handle);
    }

    /// <summary>WPF 가 창을 옮기거나 크기를 바꿀 때 — 스왑체인 크기를 클라이언트 영역(물리 픽셀)에 맞춘다.</summary>
    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (Handle != IntPtr.Zero && GetClientRect(Handle, out var client))
            Renderer?.Resize(client.Right - client.Left, client.Bottom - client.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x,
        int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
}

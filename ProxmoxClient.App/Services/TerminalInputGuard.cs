using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ProxmoxClient.App.Services;

/// <summary>
///     터미널 네이티브 창(HwndHost)으로 가는 키·마우스 입력이 WPF 에 빼앗기지 않게 한다.
///     <list type="bullet">
///         <item>
///             키: WPF 는 키 메시지를 네이티브 창에 넘기기 전에 KeyDown 으로 먼저 돌리고, 키보드 이동(KeyboardNavigation)이
///             Tab·방향키를 가져가 포커스를 옮긴다. 메시지 루프의 가장 앞(ThreadFilterMessage)에서 직접 번역·배달한다.
///         </item>
///         <item>
///             마우스: 터미널은 자기가 포커스를 가졌을 때만 클릭을 앱(btop·mc 등)의 마우스 입력으로 보내고, 아니면 글자 선택으로
///             처리한다. 창이 이미 활성이면 WM_MOUSEACTIVATE 가 오지 않아 도구 모음 버튼 등에 있던 포커스가 그대로 남으므로,
///             버튼을 누르는 순간 포커스를 먼저 터미널로 가져온다.
///         </item>
///     </list>
/// </summary>
internal sealed class TerminalInputGuard : IDisposable
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;

    private readonly HwndHost _host;

    private TerminalInputGuard(HwndHost host)
    {
        _host = host;
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
        _host.MessageHook += OnHostMessage;
    }

    public void Dispose()
    {
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
        _host.MessageHook -= OnHostMessage;
    }

    /// <summary><paramref name="root" /> 아래의 터미널 네이티브 창에 연결한다 — 아직 창이 없으면 null.</summary>
    public static TerminalInputGuard? Attach(DependencyObject root)
    {
        var host = ImeResultForwarder.FindDescendant<HwndHost>(root);
        return host is null ? null : new TerminalInputGuard(host);
    }

    private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        if (handled || msg.message is not (WmKeyDown or WmKeyUp)) return;
        if (msg.hwnd == IntPtr.Zero || msg.hwnd != _host.Handle) return;
        if (!IsTerminalKey((int)msg.wParam)) return;

        // Tab 은 WM_CHAR('\t')로 입력되므로 번역까지 해 준다(방향키는 번역해도 문자가 생기지 않는다)
        TranslateMessage(ref msg);
        DispatchMessage(ref msg);
        handled = true;
    }

    /// <summary>터미널 창 자체의 메시지 — 네이티브 처리보다 먼저 불린다.</summary>
    private IntPtr OnHostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmLButtonDown or WmRButtonDown or WmMButtonDown && GetFocus() != hwnd)
        {
            _host.Focus(); // WPF 포커스도 맞춰 두어야 도구 모음 버튼이 포커스를 되찾지 않는다
            SetFocus(hwnd);
        }

        return IntPtr.Zero;
    }

    /// <summary>WPF 키보드 이동이 가로채는 키 — Tab, PageUp·PageDown·End·Home, 방향키.</summary>
    private static bool IsTerminalKey(int virtualKey)
    {
        const int VkTab = 0x09;
        const int VkPageUp = 0x21;
        const int VkDown = 0x28;
        return virtualKey == VkTab || virtualKey is >= VkPageUp and <= VkDown;
    }

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);
}

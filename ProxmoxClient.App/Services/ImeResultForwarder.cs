using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ProxmoxClient.App.Services;

/// <summary>
///     Windows Terminal WPF 컨트롤은 IME 메시지를 처리하지 않아, 한글 등 조합 입력이 확정되는 순간 사라진다.
///     컨트롤이 호스팅하는 네이티브 창(HwndHost)의 메시지를 가로채 IME 확정 문자열(GCS_RESULTSTR)을
///     직접 전달하고, 뒤따르는 WM_IME_CHAR 는 삼켜 중복 입력을 막는다.
///     조합 중 표시는 기본 IME 창이 그대로 담당한다(WM_IME_COMPOSITION 자체는 기본 처리로 넘김).
/// </summary>
internal sealed class ImeResultForwarder : IDisposable
{
    private const int WmImeComposition = 0x010F;
    private const int WmImeChar = 0x0286;
    private const int GcsResultStr = 0x0800;

    private readonly HwndHost _host;
    private readonly Action<string> _sendText;

    private ImeResultForwarder(HwndHost host, Action<string> sendText)
    {
        _host = host;
        _sendText = sendText;
        _host.MessageHook += OnMessage;
    }

    public void Dispose()
    {
        _host.MessageHook -= OnMessage;
    }

    /// <summary>
    ///     <paramref name="root" /> 아래에서 네이티브 창을 호스팅하는 HwndHost 를 찾아 연결한다.
    ///     컨트롤이 아직 네이티브 창을 만들지 않았으면 null.
    /// </summary>
    public static ImeResultForwarder? Attach(DependencyObject root, Action<string> sendText)
    {
        var host = FindDescendant<HwndHost>(root);
        return host is null ? null : new ImeResultForwarder(host, sendText);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmImeComposition when (lParam.ToInt64() & GcsResultStr) != 0:
                // 한국어 IME 는 한 메시지에 "이전 음절 확정 + 새 음절 조합"을 함께 담으므로
                // 확정 문자열만 꺼내 보내고, 조합 표시를 위해 기본 처리는 그대로 진행한다
                if (ReadResultString(hwnd) is { Length: > 0 } text) _sendText(text);

                break;

            case WmImeChar:
                // 확정 문자열은 위에서 이미 보냈다 — 기본 처리가 만들 WM_CHAR 중복을 차단
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private static string? ReadResultString(IntPtr hwnd)
    {
        var context = ImmGetContext(hwnd);
        if (context == IntPtr.Zero) return null;

        try
        {
            var byteCount = ImmGetCompositionStringW(context, GcsResultStr, null, 0);
            if (byteCount <= 0) return null;

            var charCount = byteCount / sizeof(char);
            var buffer = ArrayPool<char>.Shared.Rent(charCount);
            try
            {
                var written = ImmGetCompositionStringW(context, GcsResultStr, buffer, byteCount);
                return written <= 0 ? null : new string(buffer, 0, Math.Min(charCount, written / sizeof(char)));
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }
        finally
        {
            ImmReleaseContext(hwnd, context);
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;

            if (FindDescendant<T>(child) is { } nested) return nested;
        }

        return null;
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hwnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr context);

    /// <summary>반환값·dwBufLen 은 문자 수가 아니라 바이트 수.</summary>
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern int
        ImmGetCompositionStringW(IntPtr context, int index, char[]? buffer, int bufferLengthBytes);
}
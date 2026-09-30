using System.Runtime.InteropServices;
using System.Text;

namespace ProxmoxClient.Core.Rdp;

/// <summary>IronRDP 클립보드 백엔드의 숨은 창(이 스레드의 "IronRDPClipboardMonitor" 창) 찾기·비우기·없애기.</summary>
internal static class ClipboardWindows
{
    private const string WindowClass = "IronRDPClipboardMonitor";

    /// <summary>지금 스레드의 백엔드 창 목록 — 만들기 전후를 비교해 이번 세션의 창을 찾는다.</summary>
    public static List<IntPtr> Find()
    {
        var found = new List<IntPtr>();
        var name = new StringBuilder(64);
        EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
        {
            name.Clear();
            if (GetClassName(window, name, name.Capacity) > 0 && name.ToString() == WindowClass) found.Add(window);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>PC 클립보드의 주인이 이 창이면(게스트 형식을 걸어 둠) 비운다.</summary>
    public static void EmptyIfOwner(IntPtr window)
    {
        if (GetClipboardOwner() != window || !OpenClipboard(window)) return;

        try
        {
            EmptyClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static void Destroy(IntPtr window)
    {
        DestroyWindow(window);
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr param);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
}

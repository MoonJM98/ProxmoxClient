using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Files;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     게스트 파일 창 — 콘솔 창의 [파일] 로 연다. 콘솔을 가리지 않게 따로 뜨고(콘솔 창 옆, 자리가 없으면 겹쳐),
///     콘솔 창에 딸려 있어 콘솔을 닫으면 함께 닫힌다. 콘솔 창마다 하나 — 다시 누르면 앞으로 가져온다.
/// </summary>
public partial class GuestFilesWindow : Window
{
    private const double Gap = 8;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private GuestFilesWindow(Window owner, string guestTitle)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Owner = owner;
        Title = Loc.T("GuestFiles_WindowTitle", guestTitle);
        PlaceBeside(owner);
        SourceInitialized += (_, _) => MatchOwnerDpi(owner);
        Closing += (_, _) => Panel.Close();
    }

    /// <summary>파일 시스템을 열 수 있는 창.</summary>
    public static GuestFilesWindow Create(Window owner, string guestTitle,
        Func<CancellationToken, Task<IGuestFileSystem>> open)
    {
        var window = new GuestFilesWindow(owner, guestTitle);
        window.Panel.Attach(open);
        window.Loaded += async (_, _) => await window.Panel.ShowAsync();
        return window;
    }

    /// <summary>파일들을 지금 폴더로 올린다 — 아직 연결 전이면 연결을 기다린다.</summary>
    public async Task UploadAsync(IReadOnlyList<string> paths)
    {
        await Panel.ShowAsync();
        await Panel.UploadAsync(paths);
    }

    /// <summary>
    ///     창 핸들이 생긴 뒤(아직 보이기 전) — 배율이 다른 모니터에 있는 콘솔 창 옆이면 자리를 다시 잡는다.
    ///     PerMonitorV2 에서 WPF 의 Left/Top 은 그 창이 놓인 모니터 배율로 나눈 값이라, 콘솔 창(150% 보조 모니터 등)의
    ///     Left/Top 을 그대로 쓰면 이 창은 자기 DPI(주 모니터 등)로 바꿔 엉뚱한 모니터에 놓인다.
    ///     먼저 콘솔 창 자리로 옮겨 DPI 를 맞춘 뒤(옮기는 동안 WPF 가 크기를 새 배율로 맞춘다) 같은 기준으로 다시 놓는다.
    /// </summary>
    private void MatchOwnerDpi(Window owner)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        if (handle == IntPtr.Zero || ownerHandle == IntPtr.Zero || GetDpiForWindow(handle) == GetDpiForWindow(ownerHandle)
            || !GetWindowRect(ownerHandle, out var ownerRect))
            return;

        SetWindowPos(handle, IntPtr.Zero, ownerRect.Left, ownerRect.Top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        PlaceBeside(owner);
    }

    /// <summary>
    ///     콘솔 창과 같은 모니터에서 콘솔 오른쪽(자리가 없으면 왼쪽, 그것도 없으면 콘솔 위 가운데)에 둔다.
    ///     콘솔이 최대화돼 있으면 그 모니터 가운데.
    /// </summary>
    private void PlaceBeside(Window owner)
    {
        var area = MonitorArea.WorkAreaOf(owner);
        var normal = owner.WindowState == WindowState.Normal && owner.ActualHeight > 0;
        var bounds = normal ? new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight) : area;
        Height = Math.Min(Math.Max(MinHeight, normal ? bounds.Height : Height), area.Height);
        if (!normal) bounds = new Rect(area.Left, area.Top + (area.Height - Height) / 2, area.Width, Height);
        Top = Math.Clamp(bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - Height));

        var right = bounds.Right + Gap;
        var left = bounds.Left - Width - Gap;
        if (right + Width <= area.Right)
            Left = right;
        else if (left >= area.Left)
            Left = left;
        else
            Left = Math.Clamp(bounds.Left + (bounds.Width - Width) / 2, area.Left,
                Math.Max(area.Left, area.Right - Width));
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

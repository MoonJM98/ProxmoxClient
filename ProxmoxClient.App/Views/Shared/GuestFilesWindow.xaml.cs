using System.Windows;
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

    private GuestFilesWindow(Window owner, string guestTitle)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Owner = owner;
        Title = Loc.T("GuestFiles_WindowTitle", guestTitle);
        PlaceBeside(owner);
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
}

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     운영체제별 SPICE 실행기 선택 — OS 분기는 이 파일 한 군데에서만 한다.
///     Windows: remote-viewer.exe(VirtViewer, 자동 설치 가능) · Linux: PATH 의 remote-viewer · macOS: RemoteViewer.app 또는
///     Homebrew 의 remote-viewer. Android 는 헤드 프로젝트가 <see cref="Current" /> 를 ACTION_VIEW 구현으로 바꿔 넣는다.
/// </summary>
public static class SpiceLaunchers
{
    private const string VirtViewerReleases = "https://releases.pagure.org/virt-viewer/";

    /// <summary>화면·서비스가 쓰는 실행기 — 헤드 프로젝트가 시작할 때 필요하면 바꿔 넣는다.</summary>
    private static ISpiceLauncher? _current; // 처음 쓸 때 고른다(아래 안내 필드가 먼저 준비되도록)

    public static ISpiceLauncher Current
    {
        get => _current ??= CreateDefault();
        set => _current = value;
    }

    public static ISpiceLauncher CreateDefault()
    {
        if (OperatingSystem.IsWindows())
            return new DesktopSpiceLauncher(WindowsRemoteViewerLocator.Detect, WindowsGuide,
                new WindowsVirtViewerInstaller());

        if (OperatingSystem.IsMacOS())
            return new DesktopSpiceLauncher(MacRemoteViewerLocator.Detect, MacGuide, null);

        return new DesktopSpiceLauncher(LinuxRemoteViewerLocator.Detect, LinuxGuide, null);
    }

    private static readonly SpiceSetupGuide WindowsGuide = new(
        "VirtViewerSetupWindow_01",
        ["VirtViewerSetupWindow_05"],
        VirtViewerReleases,
        "VirtViewerSetupWindow_08",
        "ConsoleWindow_M19");

    private static readonly SpiceSetupGuide LinuxGuide = new(
        "VirtViewerSetupWindow_01",
        ["Spice_GuideLinux"],
        "https://gitlab.com/virt-viewer/virt-viewer",
        "VirtViewerSetupWindow_08",
        "Spice_InstallAskOther");

    private static readonly SpiceSetupGuide MacGuide = new(
        "VirtViewerSetupWindow_01",
        ["Spice_GuideMac"],
        "https://formulae.brew.sh/formula/virt-viewer",
        "VirtViewerSetupWindow_08",
        "Spice_InstallAskOther");
}

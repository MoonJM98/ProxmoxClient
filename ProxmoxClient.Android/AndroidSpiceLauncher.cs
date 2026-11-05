using Android.Content;
using AndroidX.Core.Content;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;

namespace ProxmoxClient.Droid;

/// <summary>
///     Android SPICE 실행기 — 서버가 준 .vv 파일(비밀번호 포함)을 앱 캐시의 spice 폴더에 쓰고, FileProvider 주소로
///     ACTION_VIEW 인텐트를 보낸다(MIME application/x-virt-viewer). aSPICE·Opaque 같은 SPICE 앱이 그 파일을 연다.
///     열 앱이 없으면 <see cref="SpiceOpenState.ViewerMissing" /> — 설치 안내 화면을 보인다.
///     파일은 2분 뒤 지운다(앱이 그 전에 읽는다). 임시 파일 경로는 FileProvider 가 허용한 cache 하위만 쓴다.
/// </summary>
internal sealed class AndroidSpiceLauncher(Context context) : ISpiceLauncher
{
    private const string MimeType = "application/x-virt-viewer";

    /// <summary>manifest 의 FileProvider authority 와 같아야 한다.</summary>
    private const string Authority = "io.github.moonjm98.proxmoxclient.spicefiles";

    private static readonly TimeSpan FileLifetime = TimeSpan.FromMinutes(2);

    public SpiceSetupGuide Guide { get; } = new(
        "Spice_TitleAndroid",
        ["Spice_GuideAndroid"],
        "https://play.google.com/store/search?q=SPICE&c=apps",
        "Spice_PageStore",
        "Spice_InstallAskAndroid");

    /// <summary>Android 은 자동 설치를 하지 않는다 — 앱 스토어에서 받는다.</summary>
    public ISpiceViewerInstaller? Installer => null;

    public SpiceViewerStatus Probe()
    {
        var label = ResolveViewerLabel();
        return new SpiceViewerStatus(label is not null, label);
    }

    public Task<SpiceOpenResult> OpenAsync(string vvContent, string title, Action<string> report)
    {
        var viewer = ResolveViewerLabel();
        if (viewer is null) return Task.FromResult(new SpiceOpenResult(SpiceOpenState.ViewerMissing));

        string? path = null;
        try
        {
            var dir = Path.Combine(context.CacheDir!.AbsolutePath, "spice");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"pve-spice-{Guid.NewGuid():N}.vv");
            File.WriteAllText(path, vvContent);

            var uri = FileProvider.GetUriForFile(context, Authority, new Java.IO.File(path));
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, MimeType);
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
            context.StartActivity(intent);

            report(Loc.T("ConsoleWindow_M16", viewer));
            _ = DeleteLaterAsync(path);
            return Task.FromResult(new SpiceOpenResult(SpiceOpenState.Started));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or Java.Lang.RuntimeException or ActivityNotFoundException)
        {
            if (path is not null) TryDelete(path);
            return Task.FromResult(new SpiceOpenResult(SpiceOpenState.Failed, Loc.T("ConsoleWindow_M18", ex.Message)));
        }
    }

    /// <summary>.vv 을 열 수 있는 앱의 이름(없으면 null). 패키지 가시성 규칙 때문에 manifest 의 queries 가 필요하다.</summary>
    private string? ResolveViewerLabel()
    {
        var pm = context.PackageManager;
        if (pm is null) return null;

        var probe = new Intent(Intent.ActionView);
        probe.SetType(MimeType);
        var component = probe.ResolveActivity(pm);
        var package = component?.PackageName;
        if (string.IsNullOrEmpty(package) || package == "android") return null;

        try
        {
            return pm.GetApplicationLabel(pm.GetApplicationInfo(package, 0))?.ToString() ?? package;
        }
        catch (Android.Content.PM.PackageManager.NameNotFoundException)
        {
            return package;
        }
    }

    private static async Task DeleteLaterAsync(string path)
    {
        await Task.Delay(FileLifetime);
        TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 캐시 폴더라 시스템이 치워도 된다
        }
    }
}

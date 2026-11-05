using System.Runtime.CompilerServices;
using ProxmoxClient.Core.Settings;
using ProxmoxClient.Core.Vnc;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.SpiceSetup;

namespace ProxmoxClient.UI.Views.Settings;

/// <summary>
///     앱 설정·콘솔 설정·SPICE 설치 화면의 진입점 — 클러스터 "관리" 목록(모바일)과 검증용 미리보기(tools/UiSnapshot,
///     target "preview:settings"). 데스크톱은 메인 화면의 "설정" 단추가 <see cref="AppSettingsPage" /> 를 연다.
/// </summary>
internal static class SettingsModule
{
    /// <summary>클러스터 "관리" 항목 식별자 — 데스크톱 "만들기" 메뉴는 이 항목을 빼고 보여 준다(MainViewModel).</summary>
    internal const string AppSettingsFeatureId = "app-settings";

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255",
        Justification = "포팅 가이드가 정한 등록 방식 — 검증 도구가 모듈 로드 때 미리보기를 찾는다.")]
    internal static void Register()
    {
        FeatureRegistry.ClusterFeatures.Add(new ClusterFeature(AppSettingsFeatureId, "MainWindow_04", "IconSettings", 90,
            async _ => await OpenAppSettingsAsync(null)));

        PreviewRegistry.Add("settings", new PreviewTab("app", "Settings_General", "IconSettings",
            _ => new AppSettingsPage(new AppSettings())));
        PreviewRegistry.Add("settings", new PreviewTab("console-vnc", "ConsoleSettings_TabVnc", "IconMonitor",
            _ => new ConsoleSettingsPage(new ConsoleSettings(), ConsoleSettingsTab.Vnc)));
        PreviewRegistry.Add("settings", new PreviewTab("console-rdp", "ConsoleSettings_TabRdp", "IconMonitor",
            _ => new ConsoleSettingsPage(new ConsoleSettings(), ConsoleSettingsTab.Rdp)));
        PreviewRegistry.Add("settings", new PreviewTab("console-terminal", "ConsoleSettings_TabTerminal", "IconTerminal",
            _ => new ConsoleSettingsPage(new ConsoleSettings(), ConsoleSettingsTab.Terminal)));
        PreviewRegistry.Add("settings", new PreviewTab("spice", "VirtViewerSetupWindow_01", "IconExternal",
            _ => new SpiceSetupPage()));
    }

    /// <summary>앱 설정 화면을 새 페이지로 연다 — 저장된 설정을 읽어 채운다. 저장 뒤 <paramref name="saved" /> 를 부른다.</summary>
    internal static async Task OpenAppSettingsAsync(Func<Task>? saved)
    {
        var current = await new AppSettingsStore().LoadAsync();
        await AppServices.Navigator.PushViewAsync(L.T("MainWindow_04"), new AppSettingsPage(current, saved));
    }
}

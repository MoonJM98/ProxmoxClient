using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Datacenter;

/// <summary>데이터센터 탐색 화면(WPF DatacenterNavigator 와 같은 탭·순서·권한 규칙)을 앱에 연결한다.</summary>
internal static class DatacenterModule
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "기능 등록 — 이식 가이드의 진입점 등록 방식")]
    internal static void Register()
    {
        FeatureRegistry.Datacenter = session =>
            DatacenterNavigator.Create(session.Api, session.Permissions, openResource: null);
    }
}

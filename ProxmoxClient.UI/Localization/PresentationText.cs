using System.Runtime.CompilerServices;
using ProxmoxClient.Presentation;

namespace ProxmoxClient.UI.Localization;

/// <summary>공용 화면 로직(ProxmoxClient.Presentation)이 이 앱의 문구(<see cref="L" />)를 읽게 한다.</summary>
internal static class PresentationText
{
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255",
        Justification = "공용 코드는 이 어셈블리의 코드에서만 불리므로, 어셈블리가 처음 쓰일 때 한 번 연결하면 된다")]
    internal static void Register()
    {
        UiText.Use(L.T, L.T);
    }
}

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace ProxmoxClient.UI;

/// <summary>
///     앱 글꼴 — Pretendard(한글·라틴, OFL-1.1)와 JetBrains Mono(터미널)를 Assets/Fonts 에 넣어 쓴다. Pretendard 가 기본 글꼴이다. 기기 글꼴에 기대면 한글이
///     네모로 나오는 기기가 있다(실제 Android 폰에서 확인). 라틴 글자는 Pretendard 가 Inter 를 바탕으로 해 모양이 같다.
///     모든 헤드(데스크톱·Android·검사 도구)가 <see cref="UseAppFonts" /> 를 부른다.
/// </summary>
public static class AppFonts
{
    public const string FamilyName = "fonts:App#Pretendard";

    /// <summary>터미널 고정폭 글꼴(JetBrains Mono) — 기기마다 모노 글꼴이 달라 칸 폭이 흔들리지 않게 넣어 둔다.</summary>
    public const string MonoFamilyName = "fonts:App#JetBrains Mono";

    public static FontFamily Family { get; } = new(FamilyName);

    public static AppBuilder UseAppFonts(this AppBuilder builder)
    {
        return builder
            .ConfigureFonts(manager => manager.AddFontCollection(new EmbeddedFontCollection(
                new Uri("fonts:App", UriKind.Absolute),
                new Uri("avares://ProxmoxClient.UI/Assets/Fonts", UriKind.Absolute))))
            .With(new FontManagerOptions
            {
                DefaultFamilyName = FamilyName,
                // 앱에 넣은 글꼴에 없는 글자(이모지·한자 일부)는 기기 글꼴로
                FontFallbacks = [new FontFallback { FontFamily = Family }]
            });
    }
}

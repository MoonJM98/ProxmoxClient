namespace ProxmoxClient.UI.Platform;

/// <summary>화면 보호를 실제로 거는 쪽(헤드 프로젝트) — Android 는 액티비티 창에 FLAG_SECURE.</summary>
public interface IScreenSecurityHost
{
    /// <summary>켜면 최근 앱 미리보기를 가리고 화면 캡처·녹화를 막는다. UI 스레드에서 불린다.</summary>
    void Apply(bool enabled);
}

/// <summary>
///     화면 보호(설정 "화면 보호") — 비밀번호·콘솔 화면이 최근 앱 목록 미리보기나 캡처에 남지 않게 한다.
///     <see cref="Host" /> 가 없는 플랫폼(데스크톱·iOS)에서는 설정 항목을 보이지 않는다.
/// </summary>
public static class ScreenSecurity
{
    private static IScreenSecurityHost? _host;

    public static IScreenSecurityHost? Host
    {
        get => _host;
        set
        {
            _host = value;
            _host?.Apply(Enabled);
        }
    }

    public static bool IsSupported => Host is not null;

    /// <summary>지금 켜져 있는지 — 헤드는 새 액티비티를 만들 때 이 값을 다시 건다.</summary>
    public static bool Enabled { get; private set; }

    public static void Set(bool enabled)
    {
        Enabled = enabled;
        Host?.Apply(enabled);
    }
}

namespace ProxmoxClient.Mobile.Services;

/// <summary>앱이 화면에 보이는지 — 배경으로 가면 주기 새로 고침을 멈춰 배터리·데이터를 아낀다.</summary>
public static class AppLifecycle
{
    public static bool IsForeground { get; private set; } = true;

    public static event Action<bool>? ForegroundChanged;

    internal static void SetForeground(bool foreground)
    {
        if (IsForeground == foreground) return;

        IsForeground = foreground;
        ForegroundChanged?.Invoke(foreground);
    }
}

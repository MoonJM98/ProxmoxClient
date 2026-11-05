using System.Diagnostics;

namespace ProxmoxClient.UI.Views.Guest.Hardware;

/// <summary>
///     하드웨어 화면의 진단 기록 — WPF 의 App.Log 자리. UI 에는 아직 로그 파일이 없어 추적(Trace) 출력으로 남긴다.
/// </summary>
internal static class HardwareLog
{
    public static void Warn(string message)
    {
        Trace.TraceWarning(message);
    }
}

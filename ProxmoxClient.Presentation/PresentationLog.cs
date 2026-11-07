using System.Diagnostics;

namespace ProxmoxClient.Presentation;

/// <summary>
///     공용 화면 로직의 진단 기록 — 조회 실패처럼 화면은 계속 쓰되 남겨 둘 일.
///     WPF 앱은 자기 로그 파일(App.Log)을 <see cref="Sink" /> 로 넣고, 넣지 않으면 추적(Trace) 출력으로 남긴다.
/// </summary>
public static class PresentationLog
{
    public static Action<string> Sink { get; set; } = message => Trace.TraceWarning(message);

    public static void Warn(string message)
    {
        Sink(message);
    }
}

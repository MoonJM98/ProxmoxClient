using System.ComponentModel;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Services;

/// <summary>
///     콘솔 창용 게스트 실행 상태 감시 — 정지↔실행 전환 시 콜백(UI 스레드, 메인 새로고침이 상태를 갱신).
///     약한 이벤트로 구독해 오래 사는 게스트 객체가 닫힌 콘솔 창을 붙잡지 않는다.
/// </summary>
internal sealed class GuestRunStateMonitor : IDisposable
{
    /// <summary>시작 직후엔 VNC/termproxy 가 아직 준비되지 않으므로 간격을 두고 재시도한다.</summary>
    private const int MaxConnectAttempts = 6;

    private const string StatusPaused = "paused";
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(3);

    private readonly PveResource _guest;
    private readonly EventHandler<PropertyChangedEventArgs> _handler;
    private readonly Action<bool> _onStoppedChanged;

    public GuestRunStateMonitor(PveResource guest, Action<bool> onStoppedChanged)
    {
        _guest = guest;
        _onStoppedChanged = onStoppedChanged;
        IsStopped = IsGuestStopped(guest);
        _handler = OnGuestChanged;
        PropertyChangedEventManager.AddHandler(guest, _handler, string.Empty);
    }

    /// <summary>정지 상태(실행 중도 일시정지도 아님) — 일시정지된 VM 은 콘솔 연결이 가능하다.</summary>
    public bool IsStopped { get; private set; }

    public void Dispose()
    {
        PropertyChangedEventManager.RemoveHandler(_guest, _handler, string.Empty);
    }

    public static bool IsGuestStopped(PveResource guest)
    {
        return !guest.IsRunning && !string.Equals(guest.Status, StatusPaused, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     연결을 재시도한다. 매 시도 전 대기하고, <paramref name="shouldContinue" /> 가 false 면 중단.
    /// </summary>
    public static async Task<bool> RetryConnectAsync(Func<Task<bool>> connect, Func<bool> shouldContinue)
    {
        for (var attempt = 0; attempt < MaxConnectAttempts; attempt++)
        {
            await Task.Delay(ConnectRetryDelay);
            if (!shouldContinue()) return false;

            if (await connect()) return true;
        }

        return false;
    }

    private void OnGuestChanged(object? sender, PropertyChangedEventArgs e)
    {
        var stopped = IsGuestStopped(_guest);
        if (stopped == IsStopped) return;

        IsStopped = stopped;
        _onStoppedChanged(stopped);
    }
}
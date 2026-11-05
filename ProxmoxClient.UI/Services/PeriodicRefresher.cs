using Avalonia.Threading;

namespace ProxmoxClient.UI.Services;

/// <summary>
///     화면이 보이는 동안 일정 간격으로 새로 고친다. 이전 새로 고침이 끝나기 전에는 다시 부르지 않고,
///     앱이 배경에 있으면 건너뛴다.
/// </summary>
public sealed class PeriodicRefresher : IDisposable
{
    private readonly Func<Task> _refresh;
    private readonly DispatcherTimer _timer;
    private bool _running;

    public PeriodicRefresher(TimeSpan interval, Func<Task> refresh)
    {
        _refresh = refresh;
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += OnTick;
        AppLifecycle.ForegroundChanged += OnForegroundChanged;
    }

    public bool IsActive { get; private set; }

    public void Start()
    {
        IsActive = true;
        if (AppLifecycle.IsForeground) _timer.Start();
    }

    public void Stop()
    {
        IsActive = false;
        _timer.Stop();
    }

    public void Dispose()
    {
        Stop();
        AppLifecycle.ForegroundChanged -= OnForegroundChanged;
    }

    private void OnForegroundChanged(bool foreground)
    {
        if (!IsActive) return;

        if (foreground)
        {
            _timer.Start();
            OnTick(null, EventArgs.Empty); // 돌아오자마자 최신 상태로
        }
        else
        {
            _timer.Stop();
        }
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_running) return;

        _running = true;
        try
        {
            await _refresh();
        }
        catch
        {
            // 주기 새로 고침 실패는 호출자가 화면에 표시한다 — 타이머는 계속 돈다
        }
        finally
        {
            _running = false;
        }
    }
}

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
    private Task? _current;

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
        if (_current is not null) return; // 이전 새로 고침이 아직 도는 중

        try
        {
            await RefreshNowAsync();
        }
        catch
        {
            // 주기 새로 고침 실패는 호출자가 화면에 표시한다 — 타이머는 계속 돈다
        }
    }

    /// <summary>
    ///     지금 바로 새로 고친다(화면 진입·당겨서 새로 고침 등). 타이머와 같은 잠금을 써서 겹쳐 돌지 않는다 —
    ///     이미 도는 중이면 끝나기를 기다린 뒤 한 번 더 돈다(요청 뒤의 최신 상태 보장).
    /// </summary>
    public async Task RefreshNowAsync()
    {
        if (_current is { } running)
        {
            try
            {
                await running;
            }
            catch
            {
                // 앞선 새로 고침의 실패는 그쪽 호출자가 처리한다
            }

            // 기다리는 사이 다른 호출이 이미 새로 시작했으면 그것을 같이 기다린다
            if (_current is { } next)
            {
                await next;
                return;
            }
        }

        var task = _refresh();
        _current = task;
        try
        {
            await task;
        }
        finally
        {
            if (ReferenceEquals(_current, task)) _current = null;
        }
    }
}

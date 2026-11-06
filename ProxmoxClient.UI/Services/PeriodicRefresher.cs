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
    private readonly Lock _gate = new();
    private Task? _current; // 도는 새로 고침
    private Task? _pending; // 도는 것이 끝나면 한 번 더 돌 새로 고침(기다리는 요청들이 함께 쓴다)

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
        lock (_gate)
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
    public Task RefreshNowAsync()
    {
        lock (_gate)
        {
            if (_current is null) return Run();

            // 이미 도는 중 — 끝난 뒤 한 번만 더 돈다. 그사이 들어온 요청은 모두 그 한 번을 같이 기다린다
            return _pending ??= RunAfter(_current);
        }
    }

    private async Task RunAfter(Task previous)
    {
        try
        {
            await previous;
        }
        catch
        {
            // 앞선 새로 고침의 실패는 그쪽 호출자가 처리한다
        }

        Task next;
        lock (_gate)
        {
            _pending = null;
            next = _current ?? Run(); // 그사이 다른 쪽(타이머)이 이미 시작했으면 그것을 같이 기다린다
        }

        await next;
    }

    /// <summary>새로 고침 하나를 시작한다(_gate 안에서) — 끝나면 자리를 비운다.</summary>
    private Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _current = done.Task;
        _ = Body();
        return done.Task;

        async Task Body()
        {
            try
            {
                await _refresh();
                Finish(() => done.TrySetResult());
            }
            catch (Exception ex)
            {
                Finish(() => done.TrySetException(ex));
            }
        }

        void Finish(Action complete)
        {
            lock (_gate)
                if (ReferenceEquals(_current, done.Task))
                    _current = null;
            complete();
        }
    }
}

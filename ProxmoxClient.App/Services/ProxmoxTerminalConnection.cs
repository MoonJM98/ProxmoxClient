using System.Text;
using System.Windows.Threading;
using Microsoft.Terminal.Wpf;
using ProxmoxClient.Core.Terminal;

namespace ProxmoxClient.App.Services;

/// <summary>
///     Windows Terminal WPF 컨트롤(<see cref="TerminalControl" />)과 Proxmox termproxy 세션을 잇는 어댑터.
///     PTY 는 Proxmox 서버 측에 있으므로 로컬 ConPTY 없이 웹소켓 입출력만 중계한다.
///     출력은 청크마다 UI 로 보내지 않고 버퍼에 모아, 예약된 UI 작업 하나가 한 번에 내보낸다
///     (cat·dmesg 같은 대량 출력에서 디스패처 큐·메모리가 끝없이 늘고 키 입력이 밀리던 문제 방지).
/// </summary>
internal sealed class ProxmoxTerminalConnection : ITerminalConnection, IDisposable
{
    /// <summary>대량 출력 뒤 버퍼가 이 크기를 넘게 커졌으면 비운 뒤 줄여 메모리를 계속 붙잡지 않는다.</summary>
    private const int MaxRetainedBufferCapacity = 256 * 1024;

    private readonly Dispatcher _dispatcher;
    private readonly Action _flushOutput;
    private readonly object _gate = new();
    private readonly StringBuilder _outputQueue = new();

    private readonly ProxmoxTerminalSession _session;
    private bool _flushScheduled;

    /// <summary>컨트롤이 Start 를 부르기 전 출력(로그인 배너 등)은 버퍼에만 쌓는다.</summary>
    private bool _started;

    public ProxmoxTerminalConnection(ProxmoxTerminalSession session, Dispatcher dispatcher)
    {
        _session = session;
        _dispatcher = dispatcher;
        _flushOutput = FlushOutput;
        _session.Output += OnSessionOutput;
    }

    public void Dispose()
    {
        _session.Output -= OnSessionOutput;
    }

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;

            _started = true;
            ScheduleFlushLocked();
        }
    }

    public void WriteInput(string data)
    {
        _session.SendInput(data);
    }

    public void Resize(uint rows, uint columns)
    {
        _session.Resize((int)columns, (int)rows);
    }

    /// <summary>세션 수명은 창이 관리한다(재연결 시 새 연결 객체로 교체).</summary>
    public void Close()
    {
    }

    /// <summary>수신 스레드 — Span 은 호출 중에만 유효하므로 곧바로 버퍼에 복사한다.</summary>
    private void OnSessionOutput(ReadOnlySpan<char> text)
    {
        lock (_gate)
        {
            _outputQueue.Append(text);
            if (_started) ScheduleFlushLocked();
        }
    }

    private void ScheduleFlushLocked()
    {
        if (_flushScheduled || _outputQueue.Length == 0) return;

        _flushScheduled = true;
        // 입력·렌더보다 낮은 우선순위 — 키 입력이 먼저 처리되고, 그 사이 도착한 출력은 같은 작업에 합쳐진다
        _dispatcher.BeginInvoke(DispatcherPriority.Background, _flushOutput);
    }

    private void FlushOutput()
    {
        string text;
        lock (_gate)
        {
            _flushScheduled = false;
            if (_outputQueue.Length == 0) return;

            text = _outputQueue.ToString();
            _outputQueue.Clear();
            if (_outputQueue.Capacity > MaxRetainedBufferCapacity) _outputQueue.Capacity = MaxRetainedBufferCapacity;
        }

        TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(text));
    }
}
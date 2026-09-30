using System.Text;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Files;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Terminal;

/// <summary>셸 명령 결과 — 종료 코드와 표준 오류(표준 출력은 호출자가 넘긴 스트림으로 받는다).</summary>
public sealed record ShellResult(int ExitCode, string Error);

/// <summary>
///     화면에 보이지 않는 노드 셸(root@pam 로그인 셸 bash, termproxy) — 앱이 명령을 보내고 결과를 받는 통로.
///     입력 표시·신호 문자·흐름 제어·줄 편집·셸 기록을 끈다. 명령은 base64 로 보내 셸이 풀어 실행한다 — tty 는
///     base64 글자만 보므로 파일 이름의 제어 문자(^U 줄 지우기·^C 등)가 명령 줄을 바꾸지 못한다.
///     표준 출력은 시작·끝 표시 사이에 base64 로, 종료 코드·표준 오류는 끝 표시에 실어 받는다. 명령은 한 번에 하나씩.
/// </summary>
public sealed class HiddenShell : IDisposable
{
    /// <summary>tty 정규 모드의 한 줄 한도(4096) — 명령 한 줄은 이보다 짧아야 한다.</summary>
    private const int MaxLine = 4000;

    /// <summary>입력으로 보낼 때 한 번에 인코딩하는 크기 — 57 의 배수라 base64 한 줄(76자)이 정확히 맞는다.</summary>
    private const int InputChunk = 57 * 1024;

    private const int MaxPendingSends = 16;
    private static readonly TimeSpan ReadyProbeInterval = TimeSpan.FromSeconds(2);
    private const int ReadyProbes = 10;

    /// <summary>
    ///     준비 줄 — 기록·줄 편집 끄기, 입력 표시·신호·확장·흐름 제어 끄기, 세션 전용 임시 폴더(끝나면 지움).
    ///     로그인 전에 버려질 수 있어 되풀이해 보내므로 임시 폴더는 한 번만 만든다.
    /// </summary>
    private const string Preamble =
        "unset HISTFILE; set +o emacs +o vi; stty -echo -isig -iexten -ixon; PS1=; PS2=; export LC_ALL=C; "
        + "[ -n \"$T\" ] || { T=$(mktemp -d /tmp/.pvc.XXXXXX); trap 'rm -rf \"$T\"' EXIT; }; ";

    private readonly StringBuilder _line = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ProxmoxTerminalSession _session;
    private int _nextId;
    private Pending? _pending;

    private HiddenShell(ProxmoxApiClient api)
    {
        _session = new ProxmoxTerminalSession(api);
        _session.Output += OnOutput;
        _session.Closed += OnClosed;
    }

    /// <summary>연결이 끊겼거나(서버·네트워크) 취소로 버려져 더 쓸 수 없다 — 새로 연다.</summary>
    public bool IsBroken { get; private set; }

    public void Dispose()
    {
        IsBroken = true;
        _session.Dispose();
        Fail(new ObjectDisposedException(nameof(HiddenShell)));
    }

    /// <summary>노드 셸을 열고 준비한다 — setup 은 먼저 실행할 셸 정의(함수 등, ';' 로 끝나는 한 줄).</summary>
    public static async Task<HiddenShell> OpenAsync(ProxmoxApiClient api, string node, string setup,
        CancellationToken ct = default)
    {
        var shell = new HiddenShell(api);
        try
        {
            await shell._session.ConnectAsync(ConsoleTarget.ForNode(node), ct).ConfigureAwait(false);
            await shell.PrepareAsync(setup, ct).ConfigureAwait(false);
            return shell;
        }
        catch
        {
            shell.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     로그인 셸이 뜨기 전 입력은 버려질 수 있으므로 준비 줄을 준비됐다는 답이 올 때까지 되풀이해 보낸다.
    ///     첫 줄은 줄 편집기가 받아 되찍지만(보이는 곳이 없다) 그 뒤로는 입력 표시가 꺼진다.
    /// </summary>
    private async Task PrepareAsync(string setup, CancellationToken ct)
    {
        var line = Preamble + setup + " printf '__PVC_%s__\\n' READY\n";
        if (line.Length > MaxLine) throw new ArgumentException("setup line too long", nameof(setup));

        for (var i = 0; i < ReadyProbes && !_ready.Task.IsCompleted; i++)
        {
            _session.SendInput(line);
            try
            {
                await _ready.Task.WaitAsync(ReadyProbeInterval, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 아직 로그인 중 — 다시 보낸다
            }
        }

        if (!_ready.Task.IsCompletedSuccessfully)
            throw new TimeoutException(Localization.Res.T("HiddenShell_NotReady"));
    }

    /// <summary>
    ///     입력으로 보내는 전체 문자 수 — base64 76자 줄마다 앞에 '#', 뒤에 줄바꿈. 명령은 head -c 로 이만큼 받고
    ///     tr -d '#' 로 지운 뒤 푼다. 명령이 먼저 실패해 남은 줄이 셸로 가도 '#' 로 시작해 주석이 된다.
    /// </summary>
    public static long EncodedLength(long length)
    {
        var chars = (length + 2) / 3 * 4;
        return chars + 2 * ((chars + 75) / 76);
    }

    /// <summary>
    ///     명령을 실행한다. 표준 출력은 output 에(없으면 버림), input 이 있으면 명령 뒤에 length 바이트를
    ///     <see cref="EncodedLength" /> 형식으로 흘려 보낸다. 취소하면 원격 명령은 멈출 수 없으므로 셸을 버린다.
    /// </summary>
    public async Task<ShellResult> RunAsync(string script, Stream? output = null, Stream? input = null,
        long inputLength = 0, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        await _queue.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsBroken, this);
            var id = Interlocked.Increment(ref _nextId);
            var line = CommandLine(id, script);
            if (line.Length > MaxLine) throw new GuestFileException(Localization.Res.T("HiddenShell_TooLong"));

            var pending = new Pending(id, output, input is null ? progress : null);
            lock (_lock) _pending = pending;
            using var registration = ct.Register(() => Abandon(pending));
            _session.SendInput(line);
            if (input is not null)
            {
                try
                {
                    await SendInputAsync(input, inputLength, progress, ct).ConfigureAwait(false);
                }
                catch
                {
                    Abandon(pending); // 입력을 다 못 보냈다 — 원격이 남은 입력을 기다리므로 셸을 버린다
                    throw;
                }
            }

            try
            {
                return await pending.Done.Task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 출력을 못 받았다(받는 파일 쓰기 실패·알아볼 수 없는 출력) — 원격 명령은 계속 돌 수 있으므로 셸을 버린다
                IsBroken = true;
                _session.Dispose();
                throw;
            }
        }
        finally
        {
            _queue.Release();
        }
    }

    /// <summary>
    ///     명령 한 줄 — 시작 표시, 스크립트(base64 로 실어 셸이 풀어 eval)의 표준 출력을 base64 로, 끝 표시에 종료 코드와
    ///     표준 오류(base64). 표시 문자열은 printf 인자로 나눠 두어 명령 글자 그대로는 표시가 되지 않는다.
    /// </summary>
    internal static string CommandLine(int id, string script)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        return $"printf '\\n__PVC_%s_%s__\\n' B {id}; ( eval \"$(printf %s {encoded} | base64 -d)\" ) "
               + $"2>\"$T/e{id}\" | base64; printf '\\n__PVC_%s_%s__ %s %s\\n' E {id} \"${{PIPESTATUS[0]}}\" "
               + $"\"$(base64 -w0 < \"$T/e{id}\")\"; rm -f \"$T/e{id}\"\n";
    }

    /// <summary>
    ///     입력을 '#' + base64 76자 줄로 정확히 length 바이트만 보낸다(그사이 파일이 줄면 실패 — 원격 head 가
    ///     기다리지 않게 셸을 버린다). 보낼 줄이 쌓이면 기다려 메모리에 다 올리지 않는다.
    /// </summary>
    private async Task SendInputAsync(Stream input, long length, IProgress<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[InputChunk];
        var builder = new StringBuilder(InputChunk / 3 * 4 + InputChunk / 57 * 2 + 8);
        long sent = 0;
        while (sent < length)
        {
            var want = (int)Math.Min(buffer.Length, length - sent);
            var read = await input.ReadAtLeastAsync(buffer.AsMemory(0, want), want, false, ct).ConfigureAwait(false);
            if (read < want) throw new IOException(Localization.Res.T("HiddenShell_InputChanged"));

            var encoded = Convert.ToBase64String(buffer, 0, read);
            builder.Clear();
            for (var i = 0; i < encoded.Length; i += 76)
                builder.Append('#').Append(encoded, i, Math.Min(76, encoded.Length - i)).Append('\n');

            while (_session.PendingSends > MaxPendingSends && !IsBroken)
                await Task.Delay(5, ct).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(IsBroken, this);

            _session.SendInput(builder.ToString());
            sent += read;
            progress?.Report(sent);
        }
    }

    private void OnOutput(ReadOnlySpan<char> text)
    {
        lock (_lock)
        {
            foreach (var c in text)
            {
                if (c == '\n')
                {
                    ProcessLine(_line.ToString());
                    _line.Clear();
                }
                else if (c != '\r')
                {
                    _line.Append(c);
                }
            }
        }
    }

    /// <summary>(잠금 안에서) 한 줄 — 준비 답, 명령 시작·끝 표시, 그 사이의 base64 출력.</summary>
    private void ProcessLine(string line)
    {
        if (line == "__PVC_READY__")
        {
            _ready.TrySetResult();
            return;
        }

        if (_pending is not { } pending) return;

        if (!pending.Started)
        {
            pending.Started = line == $"__PVC_B_{pending.Id}__";
            return;
        }

        var end = $"__PVC_E_{pending.Id}__";
        if (line.StartsWith(end, StringComparison.Ordinal))
        {
            _pending = null;
            pending.Done.TrySetResult(ParseEnd(line[end.Length..]));
            return;
        }

        if (line.Length > 0) pending.Write(line);
    }

    /// <summary>끝 표시 뒤: " 종료코드 표준오류(base64)".</summary>
    private static ShellResult ParseEnd(string rest)
    {
        var parts = rest.Trim().Split(' ', 2);
        var code = int.TryParse(parts[0], out var value) ? value : -1;
        var error = string.Empty;
        if (parts.Length > 1 && parts[1].Length > 0)
        {
            try
            {
                error = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])).Trim();
            }
            catch (FormatException)
            {
                error = parts[1];
            }
        }

        return new ShellResult(code, error);
    }

    private void OnClosed(Exception? error)
    {
        IsBroken = true;
        Fail(error ?? new IOException(Localization.Res.T("HiddenShell_Closed")));
    }

    /// <summary>
    ///     취소·입력 실패 — 원격 명령은 멈출 수 없으므로 이 셸은 버리고, 더 받은 출력은 호출자 스트림에 쓰지 않는다
    ///     (호출자가 곧 스트림을 닫는다).
    /// </summary>
    private void Abandon(Pending pending)
    {
        IsBroken = true;
        lock (_lock)
        {
            if (ReferenceEquals(_pending, pending)) _pending = null;
        }

        _session.Dispose();
        pending.Done.TrySetCanceled();
    }

    private void Fail(Exception error)
    {
        Pending? pending;
        lock (_lock)
        {
            pending = _pending;
            _pending = null;
        }

        pending?.Done.TrySetException(error);
        _ready.TrySetException(error);
    }

    /// <summary>실행 중인 명령 하나 — 받은 base64 줄을 풀어 출력 스트림에 쓴다. 실패한 뒤로는 쓰지 않는다.</summary>
    private sealed class Pending(int id, Stream? output, IProgress<long>? progress)
    {
        private readonly byte[] _decoded = new byte[96];
        private long _received;

        public int Id { get; } = id;
        public bool Started { get; set; }

        public TaskCompletionSource<ShellResult> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Write(string line)
        {
            if (Done.Task.IsCompleted) return; // 이미 실패 — 끝 표시까지 나머지는 버린다

            if (!Convert.TryFromBase64Chars(line, _decoded, out var count))
            {
                Done.TrySetException(new GuestFileException(Localization.Res.T("HiddenShell_BadOutput")));
                return;
            }

            try
            {
                output?.Write(_decoded, 0, count);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
            {
                Done.TrySetException(ex); // 받는 파일에 쓰지 못했다(디스크 가득·닫힘 등)
                return;
            }

            _received += count;
            progress?.Report(_received);
        }
    }
}

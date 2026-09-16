using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Vpn;

/// <summary>
///     Drives a local openvpn.exe process over its TCP management interface
///     (--management 127.0.0.1 {port} --management-hold). Exposes tunnel state,
///     live byte counters and the OpenVPN log as events.
/// </summary>
public sealed class OpenVpnManager : IDisposable
{
    private const int MgmtConnectAttempts = 20;
    private const int MgmtConnectDelayMs = 250;
    private const int ConnectTimeoutSeconds = 60;
    private const int TerminateTimeoutMs = 8000;

    private readonly object _writeLock = new();
    private bool _disposed;
    private TcpClient? _mgmtClient;
    private NetworkStream? _mgmtStream;

    private Process? _process;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;

    /// <summary>Current tunnel state (thread-safe read; transitions raise <see cref="StateChanged" />).</summary>
    public VpnState State { get; private set; } = VpnState.Disconnected;

    /// <summary>Tunnel local IP once connected (e.g. "10.8.0.2"), when reported.</summary>
    public string? VirtualIp { get; private set; }

    /// <summary>Cumulative bytes received through the tunnel.</summary>
    public long BytesIn { get; private set; }

    /// <summary>Cumulative bytes sent through the tunnel.</summary>
    public long BytesOut { get; private set; }

    /// <summary>Last fatal error text; null when none occurred.</summary>
    public string? LastError { get; private set; }

    /// <summary>OpenVPN process id while running.</summary>
    public int? ProcessId => _process is { HasExited: false } p ? p.Id : null;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Dispose는 예외를 던지지 않음
        }

        _disposed = true;
    }

    /// <summary>Raised on every tunnel state transition.</summary>
    public event Action<VpnState>? StateChanged;

    /// <summary>Raised for each OpenVPN management log line and process output line.</summary>
    public event Action<string>? LogReceived;

    /// <summary>Raised on every byte-count update.</summary>
    public event Action<long, long>? StatsUpdated;

    /// <summary>
    ///     Probes, in order: the PROXMOXCLIENT_OPENVPN environment variable,
    ///     where.exe (PATH), then the default OpenVPN install locations.
    ///     Returns the first existing openvpn.exe path, or null.
    /// </summary>
    public static string? DetectOpenVpnExe()
    {
        var candidates = new List<string>();

        var fromEnv = Environment.GetEnvironmentVariable("PROXMOXCLIENT_OPENVPN");
        if (!string.IsNullOrWhiteSpace(fromEnv)) candidates.Add(fromEnv);

        var onPath = FindOnPath();
        if (onPath is not null) candidates.Add(onPath);

        candidates.Add(@"C:\Program Files\OpenVPN\bin\openvpn.exe");
        candidates.Add(@"C:\Program Files (x86)\OpenVPN\bin\openvpn.exe");

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    ///     Starts openvpn.exe with the given .ovpn config, attaches the management
    ///     interface, releases the startup hold, and waits until the tunnel is
    ///     CONNECTED (or Error / cancellation / 60 s timeout). Throws
    ///     <see cref="OpenVpnException" /> (Korean message) and cleans up on failure.
    /// </summary>
    public async Task ConnectAsync(string configPath, string? exePath = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (State is VpnState.Connecting or VpnState.Connected or VpnState.Reconnecting)
            throw new OpenVpnException(Res.T("OpenVpnManager_01"));

        if (!File.Exists(configPath)) throw new OpenVpnException(Res.T("OpenVpnManager_02", configPath));

        var exe = exePath is { Length: > 0 } ? exePath : DetectOpenVpnExe();
        if (exe is null || !File.Exists(exe))
            throw new OpenVpnException(
                Res.T("OpenVpnManager_03") +
                Res.T("OpenVpnManager_04"));

        await CleanupAsync().ConfigureAwait(false);
        LastError = null;
        VirtualIp = null;
        BytesIn = 0;
        BytesOut = 0;

        var port = GetFreeTcpPort();
        var workingDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Environment.CurrentDirectory;

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--config \"{configPath}\" --management 127.0.0.1 {port} --management-hold --verb 3",
            WorkingDirectory = workingDir,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 }) RaiseLog(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 }) RaiseLog(e.Data);
        };
        process.Exited += (_, _) => OnProcessExited();

        _process = process;

        try
        {
            if (!process.Start()) throw new OpenVpnException(Res.T("OpenVpnManager_05"));

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            SetState(VpnState.Connecting);
            await AttachManagementAsync(process, port, ct).ConfigureAwait(false);

            await SendCommandAsync("state on").ConfigureAwait(false);
            await SendCommandAsync("log on").ConfigureAwait(false);
            await SendCommandAsync("bytecount 1").ConfigureAwait(false);
            await SendCommandAsync("version").ConfigureAwait(false);
            await SendCommandAsync("hold release").ConfigureAwait(false);

            await WaitForConnectedAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            SetState(VpnState.Disconnected);
            throw;
        }
    }

    /// <summary>
    ///     Gracefully disconnects: sends "signal SIGTERM", waits up to 8 s for the
    ///     process to exit, then force-kills. Idempotent; final state is Disconnected.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_disposed || (_process is null && State == VpnState.Disconnected)) return;

        if (State is not VpnState.Error) SetState(VpnState.Disconnecting);

        try
        {
            if (_process is { HasExited: false } && _mgmtStream is not null)
                try
                {
                    await SendCommandAsync("signal SIGTERM").ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // 관리 소켓이 이미 닫혀 있으면 프로세스 종료로 처리
                }

            if (_process is { HasExited: false } p)
            {
                var exitTask = p.WaitForExitAsync();
                var completed = await Task.WhenAny(exitTask, Task.Delay(TerminateTimeoutMs)).ConfigureAwait(false);
                if (completed != exitTask && !p.HasExited)
                    try
                    {
                        p.Kill(true);
                    }
                    catch (InvalidOperationException)
                    {
                        // 이미 종료된 경우
                    }

                try
                {
                    await exitTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // 프로세스가 경합 중 종료된 경우
                }
            }
        }
        finally
        {
            await CleanupAsync().ConfigureAwait(false);
            SetState(VpnState.Disconnected);
        }
    }

    private static string? FindOnPath()
    {
        try
        {
            var startInfo = new ProcessStartInfo("where.exe", "openvpn.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var probe = Process.Start(startInfo);
            if (probe is null) return null;

            var output = probe.StandardOutput.ReadToEnd();
            if (!probe.WaitForExit(5000)) return null;

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task AttachManagementAsync(Process process, int port, CancellationToken ct)
    {
        for (var attempt = 0;; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited) throw new OpenVpnException(LastError ?? Res.T("OpenVpnManager_06"));

            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                _mgmtClient = client;
                _mgmtStream = client.GetStream();
                break;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                client.Dispose();
                if (attempt >= MgmtConnectAttempts - 1)
                    throw new OpenVpnException(
                        Res.T("OpenVpnManager_07", port), ex);

                await Task.Delay(MgmtConnectDelayMs, ct).ConfigureAwait(false);
            }
        }

        _readCts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadManagementLoopAsync(_readCts.Token));
    }

    private async Task WaitForConnectedAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));

        while (State is VpnState.Connecting or VpnState.Reconnecting)
            try
            {
                await Task.Delay(250, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new OpenVpnException(
                    Res.T("OpenVpnManager_08", ConnectTimeoutSeconds));
            }

        if (State != VpnState.Connected) throw new OpenVpnException(LastError ?? Res.T("OpenVpnManager_09"));

        ct.ThrowIfCancellationRequested();
    }

    private async Task ReadManagementLoopAsync(CancellationToken ct)
    {
        var stream = _mgmtStream;
        if (stream is null) return;

        var reader = new StreamReader(stream, Encoding.ASCII, false,
            4096, true);

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                break; // 관리 소켓이 닫힘
            }

            if (line is null) break;

            if (line.Length > 0)
                try
                {
                    HandleManagementLine(line);
                }
                catch
                {
                    // 개별 알림 처리 실패가 루프를 중단하지 않음
                }
        }
    }

    private void HandleManagementLine(string line)
    {
        if (line.StartsWith(">STATE:", StringComparison.Ordinal))
        {
            HandleStateNotification(line);
        }
        else if (line.StartsWith(">BYTECOUNT:", StringComparison.Ordinal))
        {
            HandleBytecountNotification(line);
        }
        else if (line.StartsWith(">LOG:", StringComparison.Ordinal))
        {
            RaiseLog(ExtractLogText(line));
        }
        else if (line.StartsWith(">FATAL:", StringComparison.Ordinal))
        {
            LastError = line[">FATAL:".Length..];
            SetState(VpnState.Error);
        }
        else if (line.StartsWith(">PASSWORD:Need 'Auth'", StringComparison.Ordinal))
        {
            LastError = Res.T("OpenVpnManager_10");
            SetState(VpnState.Error);
        }

        // SUCCESS:/ERROR:/>HOLD: 및 기타 응답은 무시
    }

    private void HandleStateNotification(string line)
    {
        // >STATE:{unix},{statename},{description},{local_ip},...
        var fields = line.Split(',');
        if (fields.Length < 3) return;

        var stateName = fields[2];

        switch (stateName)
        {
            case "CONNECTED":
                // 4번째 콤마 필드(인덱스 3)는 터널에 할당된 로컬 IP
                if (fields.Length > 3 && fields[3].Length > 0) VirtualIp = fields[3];

                SetState(VpnState.Connected);
                break;

            case "CONNECTING":
            case "WAIT":
            case "AUTH":
            case "GET_CONFIG":
            case "ASSIGN_IP":
            case "ADD_ROUTES":
                SetState(VpnState.Connecting);
                break;

            case "RECONNECTING":
                SetState(VpnState.Reconnecting);
                break;

            case "EXITING":
                SetState(VpnState.Disconnecting);
                break;
        }
    }

    private void HandleBytecountNotification(string line)
    {
        // >BYTECOUNT:{bytes_down},{bytes_up} — 첫 번째 값이 수신(in)
        var values = line.AsSpan(">BYTECOUNT:".Length);
        var separator = values.IndexOf(',');
        if (separator <= 0) return;

        if (!long.TryParse(values[..separator], out var bytesDown)
            || !long.TryParse(values[(separator + 1)..], out var bytesUp))
            return;

        BytesIn = bytesDown;
        BytesOut = bytesUp;
        StatsUpdated?.Invoke(BytesIn, BytesOut);
    }

    private static string ExtractLogText(string line)
    {
        // >LOG:{unix},{flags},{text} — text에 콤마가 포함될 수 있음
        var remainder = line.AsSpan(">LOG:".Length);
        var firstComma = remainder.IndexOf(',');
        if (firstComma < 0) return remainder.ToString();

        var afterUnix = remainder[(firstComma + 1)..];
        var secondComma = afterUnix.IndexOf(',');
        return secondComma < 0 ? afterUnix.ToString() : afterUnix[(secondComma + 1)..].ToString();
    }

    private void OnProcessExited()
    {
        if (State != VpnState.Error) SetState(VpnState.Disconnected);
    }

    private Task SendCommandAsync(string command)
    {
        var stream = _mgmtStream;
        if (stream is null) return Task.CompletedTask;

        var payload = Encoding.ASCII.GetBytes(command + "\n");
        try
        {
            lock (_writeLock)
            {
                if (stream.CanWrite)
                {
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();
                }
            }

            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return Task.FromException(ex);
        }
    }

    private async Task CleanupAsync()
    {
        try
        {
            _readCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_readLoop is not null)
            try
            {
                await Task.WhenAny(_readLoop, Task.Delay(500)).ConfigureAwait(false);
            }
            catch
            {
            }

        _readCts?.Dispose();
        _readCts = null;
        _readLoop = null;

        _mgmtStream?.Dispose();
        _mgmtStream = null;
        _mgmtClient?.Dispose();
        _mgmtClient = null;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }

            _process.Dispose();
            _process = null;
        }
    }

    private void SetState(VpnState newState)
    {
        if (State == newState) return;

        State = newState;
        StateChanged?.Invoke(newState);
    }

    private void RaiseLog(string text)
    {
        LogReceived?.Invoke(text);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
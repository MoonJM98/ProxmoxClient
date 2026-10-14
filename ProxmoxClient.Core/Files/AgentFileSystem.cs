using System.Globalization;
using System.Text;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Localization;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Files;

/// <summary>
///     VM 파일 시스템 — QEMU 게스트 에이전트의 명령 실행(exec)으로 게스트 안에서 다룬다(Linux: sh, Windows: PowerShell).
///     받기는 3 MiB 조각을 base64 출력으로, 올리기는 약 45 KB 조각을 표준 입력으로 보내 게스트 임시 파일에 이어 붙인 뒤
///     제자리로 옮긴다(도중에 실패하면 임시 파일을 지운다). 에이전트가 exec 를 막아 두었으면 열 때 알린다.
/// </summary>
public sealed class AgentFileSystem : IGuestFileSystem
{
    /// <summary>받기 조각 — 에이전트 출력 한도(16 MiB) 안에서 base64 로 넉넉하게.</summary>
    private const int ReadChunk = 3 * 1024 * 1024;

    /// <summary>올리기 조각(원본 바이트) — base64 로 60,000 자, 서버의 input-data 한도 안.</summary>
    private const int WriteChunk = 45_000;

    /// <summary>목록 한 페이지 — 첫 화면이 곧 보일 만큼, Windows(PowerShell 시작)가 너무 여러 번 돌지 않을 만큼.</summary>
    private const int ListPage = 1000;

    /// <summary>조각 이름은 6자리(p000000) — 그보다 많아질 큰 파일(약 45GB 이상)은 명령 방식으로.</summary>
    private const int MaxParts = 999_999;

    /// <summary>한 번에 잇는 조각 수(약 90MB) — 느린 디스크에서도 명령 하나가 몇 분을 넘지 않게.</summary>
    private const int AssembleBatch = 2000;

    /// <summary>에이전트 파일 API(file-read) 로 한 번에 받을 수 있는 크기 — 서버 한도 16 MiB.</summary>
    private const long FileReadLimit = 16 * 1024 * 1024;

    private readonly ProxmoxApiClient _api;
    private readonly IAgentDialect _dialect;
    private readonly AgentExec _exec;
    private readonly string _node;
    private readonly int _vmid;

    // 에이전트 파일 API(guest-file-*) — 명령 실행·폴링 없이 한 번에 읽고 쓴다. 막혔거나 권한이 없으면 명령으로
    private bool _fileRead;
    private bool _fileWrite;

    private AgentFileSystem(ProxmoxApiClient api, string node, int vmid, IAgentDialect dialect,
        IReadOnlySet<string> commands)
    {
        _api = api;
        _node = node;
        _vmid = vmid;
        _exec = new AgentExec(api, node, vmid);
        _dialect = dialect;
        var files = commands.Contains("guest-file-open") && commands.Contains("guest-file-close");
        _fileRead = files && commands.Contains("guest-file-read");
        _fileWrite = files && commands.Contains("guest-file-write");
    }

    public char Separator => _dialect.Separator;
    public StringComparer NameComparer => _dialect.NameComparer;
    public string HomePath => _dialect.Home;
    public bool CanDownloadDirectory => _dialect.CanTar;

    public void Dispose()
    {
    }

    /// <summary>
    ///     에이전트가 명령 실행을 허용하는지 확인하고, 게스트가 Windows 인지 Linux 등인지에 맞춰 연다 —
    ///     에이전트가 알려 준 OS(get-osinfo)를 먼저, 없으면 VM 설정의 ostype 을 본다.
    /// </summary>
    public static async Task<AgentFileSystem> OpenAsync(ProxmoxApiClient api, string node, int vmid,
        CancellationToken ct = default)
    {
        var configTask = ConfigOsTypeAsync(api, node, vmid, ct);
        var (osId, commands) = await api.GetAgentCapabilitiesAsync(node, vmid, ct).ConfigureAwait(false);
        var osType = await configTask.ConfigureAwait(false);
        if (!commands.Contains("guest-exec") || !commands.Contains("guest-exec-status"))
        {
            // 파일 쓰기가 열려 있으면 해제 스크립트를 써 두어 한 줄로 실행하게 한다(못 쓰면 전체 명령을 보인다)
            var family = Family(osId, osType);
            var run = await AgentUnblock.TryWriteScriptAsync(api, node, vmid, family, commands, ct)
                .ConfigureAwait(false);
            throw new AgentExecBlockedException(family, run);
        }

        IAgentDialect dialect = IsWindows(osId, osType) ? new WindowsAgentDialect() : new PosixAgentDialect();
        return new AgentFileSystem(api, node, vmid, dialect, commands);
    }

    /// <summary>
    ///     Windows 게스트인지 — 에이전트 OS id("mswindows" 등)가 있으면 그것으로, 없으면 VM 설정 ostype
    ///     (wxp·w2k·w2k3·w2k8·wvista·win7·win8·win10·win11 → Windows, l24·l26·solaris·other → 그 밖).
    /// </summary>
    internal static bool IsWindows(string agentOsId, string configOsType)
    {
        if (agentOsId.Length > 0) return agentOsId.Equals("mswindows", StringComparison.OrdinalIgnoreCase);

        return configOsType.StartsWith('w');
    }

    /// <summary>RHEL 계열 get-osinfo id(에이전트 기본 설정이 명령 실행을 막는 배포판).</summary>
    private static readonly HashSet<string> RedHatIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "rhel", "centos", "ol", "rocky", "almalinux", "fedora", "amzn", "eurolinux", "circle", "anolis"
    };

    /// <summary>게스트 OS 갈래 — 에이전트 OS id 가 없으면(osinfo 도 막힘) Windows 가 아닌 것은 Linux 로.</summary>
    internal static GuestOsFamily Family(string agentOsId, string configOsType)
    {
        if (IsWindows(agentOsId, configOsType)) return GuestOsFamily.Windows;

        return RedHatIds.Contains(agentOsId) ? GuestOsFamily.RedHat : GuestOsFamily.Linux;
    }

    /// <summary>VM 설정의 ostype — 읽지 못하면 빈 문자열(에이전트 OS 로만 판단).</summary>
    private static async Task<string> ConfigOsTypeAsync(ProxmoxApiClient api, string node, int vmid,
        CancellationToken ct)
    {
        try
        {
            var config = await api.GetGuestConfigAsync(node, ResourceKind.Qemu, vmid, ct).ConfigureAwait(false);
            return config.GetValueOrDefault("ostype", string.Empty);
        }
        catch (ProxmoxApiException)
        {
            return string.Empty;
        }
    }

    public Task<IReadOnlyList<GuestFileEntry>> ListAsync(string directory, CancellationToken ct = default)
    {
        return ListAsync(directory, null, ct);
    }

    /// <summary>
    ///     목록을 <see cref="ListPage" /> 개씩 나눠 받는다 — 에이전트 출력은 명령이 끝나야 오므로 큰 폴더는 앞 페이지부터
    ///     보이고, 출력 한도(16 MiB)에도 걸리지 않는다. 한 개 더 달라고 해서 다음 페이지가 있는지 안다.
    ///     페이지 사이에 폴더가 바뀌면 겹친 이름은 한 번만 넣는다(다음 새로 고침이 바로잡는다).
    /// </summary>
    public async Task<IReadOnlyList<GuestFileEntry>> ListAsync(string directory,
        Action<IReadOnlyList<GuestFileEntry>>? partial, CancellationToken ct = default)
    {
        var all = new List<GuestFileEntry>();
        var seen = new HashSet<string>(NameComparer);
        for (var offset = 0;; offset += ListPage)
        {
            var output = await RunAsync(_dialect.List(directory, offset, ListPage + 1), null, ct)
                .ConfigureAwait(false);
            var page = GuestListFormat.ParseBase64(output);
            foreach (var entry in page.Take(ListPage))
                if (seen.Add(entry.Name))
                    all.Add(entry);

            if (page.Count <= ListPage) return all;

            partial?.Invoke(all.ToArray());
        }
    }

    public async Task DownloadAsync(string directory, GuestFileEntry entry, Stream destination,
        IProgress<long>? progress, CancellationToken ct = default)
    {
        var path = _dialect.Combine(directory, _dialect.CheckName(entry.Name));
        if (!entry.IsDirectory)
        {
            await ReadAllAsync(path, entry.Size, destination, progress, ct).ConfigureAwait(false);
            return;
        }

        if (!_dialect.CanTar) throw new GuestFileException(Res.T("AgentFiles_NoFolder"));

        // 폴더는 게스트 임시 tar 로 묶어 조각으로 받고 지운다
        var temp = _dialect.TempPath(_dialect.Parent(path), Guid.NewGuid().ToString("N")) + ".tar";
        try
        {
            var size = long.Parse(
                (await RunAsync(_dialect.TarToTemp(path, temp), null, ct).ConfigureAwait(false)).Trim(),
                CultureInfo.InvariantCulture);
            await ReadAllAsync(temp, size, destination, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            await CleanupAsync(temp).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     파일 전체를 받는다 — 16 MiB 이하면 에이전트 파일 API 로 한 번에(실패하면 조각으로), 아니면 조각으로.
    ///     size 는 목록의 크기(어림값)일 뿐 — 심볼릭 링크는 링크 자체 크기(대상 경로 길이·0)이고, 목록 뒤에 커진 파일도 있다.
    ///     그래서 끝은 실제로 읽은 데이터로 정한다.
    /// </summary>
    private async Task ReadAllAsync(string path, long size, Stream destination, IProgress<long>? progress,
        CancellationToken ct)
    {
        if (_fileRead && size <= FileReadLimit && await TryFileReadAsync(path, size, ct).ConfigureAwait(false)
                is { } bytes)
        {
            await destination.WriteAsync(bytes, ct).ConfigureAwait(false);
            progress?.Report(bytes.Length);
            return;
        }

        await ReadChunksAsync(path, destination, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     에이전트 파일 API 로 읽기 — 서버는 바이트를 한 글자씩(0~255) 싣는다. 잘렸거나
    ///     글자가 바이트가 아니면 null(조각 방식으로). 막혔거나 권한이 없으면 이 연결 동안 쓰지 않는다.
    /// </summary>
    private async Task<byte[]?> TryFileReadAsync(string path, long size, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> result;
        try
        {
            result = await _api.Agent.FileReadAsync(_node, _vmid, path, ct).ConfigureAwait(false);
        }
        catch (ProxmoxApiException)
        {
            _fileRead = false;
            return null;
        }

        return FileReadBytes(result);
    }

    /// <summary>
    ///     file-read 결과를 바이트로 — 잘렸거나 바이트가 아닌 글자가 있으면 null. 목록 크기와 달라도 잘리지 않았으면
    ///     그것이 지금의 파일 전체다(링크 대상·그사이 바뀐 파일).
    /// </summary>
    internal static byte[]? FileReadBytes(IReadOnlyDictionary<string, string> result)
    {
        var content = result.GetValueOrDefault("content", string.Empty);
        if (result.GetValueOrDefault("truncated") is "1" or "true" or "True" || content.Any(c => c > 0xFF))
            return null;

        return content.Select(c => (byte)c).ToArray();
    }

    /// <summary>
    ///     빈 조각(파일 끝)이 올 때까지 조각으로 읽는다 — 조각이 짧게 와도(네트워크·FUSE 파일 시스템) 이어 읽는다.
    ///     목록 크기에서 멈추지 않는다: 링크는 대상 파일 끝까지, 그사이 커진 파일은 지금 끝까지 받는다.
    /// </summary>
    private async Task ReadChunksAsync(string path, Stream destination, IProgress<long>? progress,
        CancellationToken ct)
    {
        long done = 0;
        while (true)
        {
            var text = await RunAsync(_dialect.ReadChunk(path, done, ReadChunk), null, ct).ConfigureAwait(false);
            var bytes = Convert.FromBase64String(text.Trim());
            if (bytes.Length == 0) break;

            await destination.WriteAsync(bytes, ct).ConfigureAwait(false);
            done += bytes.Length;
            progress?.Report(done);
        }
    }

    public async Task UploadAsync(string directory, string name, Stream source, long length,
        IProgress<long>? progress, CancellationToken ct = default)
    {
        _dialect.CheckName(name);
        var temp = _dialect.TempPath(directory, Guid.NewGuid().ToString("N"));
        var finished = false;
        var parts = 0;
        try
        {
            if (length <= WriteChunk && await TryFileWriteAsync(temp, source, length, ct).ConfigureAwait(false))
            {
                progress?.Report(length);
                await RunAsync(_dialect.Finish(temp, directory, name), null, ct).ConfigureAwait(false);
                finished = true;
                return;
            }

            if (length > WriteChunk && length <= (long)MaxParts * WriteChunk)
                parts = await TryWritePartsAsync(temp, source, progress, ct).ConfigureAwait(false);
            if (parts > 0)
            {
                // 묶음마다 명령 하나 — 수 GB 도 한 명령의 실행 제한(AgentExec)에 걸리지 않게
                for (var first = 0; first < parts; first += AssembleBatch)
                    await RunAsync(_dialect.Assemble(temp, first, Math.Min(AssembleBatch, parts - first)), null, ct)
                        .ConfigureAwait(false);
                await RunAsync(_dialect.Finish(temp, directory, name), null, ct).ConfigureAwait(false);
                finished = true;
                return;
            }

            await RunAsync(_dialect.Truncate(temp), null, ct).ConfigureAwait(false);
            var buffer = new byte[WriteChunk];
            long sent = 0;
            int read;
            while ((read = await source.ReadAtLeastAsync(buffer, buffer.Length, false, ct).ConfigureAwait(false)) > 0)
            {
                await RunAsync(_dialect.Append(temp), Convert.ToBase64String(buffer, 0, read), ct)
                    .ConfigureAwait(false);
                sent += read;
                progress?.Report(sent);
            }

            await RunAsync(_dialect.Finish(temp, directory, name), null, ct).ConfigureAwait(false);
            finished = true;
        }
        finally
        {
            if (!finished)
            {
                await CleanupAsync(temp).ConfigureAwait(false);
                if (parts > 0) await CleanupAsync(_dialect.RemoveParts(temp)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     큰 파일 — 에이전트 파일 API 로 조각(temp.p000000 …)을 쓴다. 조각마다 명령(셸·PowerShell)을 띄우지
    ///     않아 훨씬 빠르고, 끝에 명령 한 번으로 잇는다. 쓴 조각 수(막혔으면 0 — 원본을 되돌려 명령 방식으로).
    /// </summary>
    private async Task<int> TryWritePartsAsync(string temp, Stream source, IProgress<long>? progress,
        CancellationToken ct)
    {
        if (!_fileWrite || !source.CanSeek) return 0;

        var start = source.Position;
        var buffer = new byte[WriteChunk];
        long sent = 0;
        var parts = 0;
        int read;
        try
        {
            while ((read = await source.ReadAtLeastAsync(buffer, buffer.Length, false, ct).ConfigureAwait(false)) > 0)
            {
                var part = temp + ".p" + parts.ToString("D6", CultureInfo.InvariantCulture);
                try
                {
                    await _api.Agent.FileWriteBytesAsync(_node, _vmid, part, buffer.AsMemory(0, read), ct)
                        .ConfigureAwait(false);
                }
                catch (ProxmoxApiException) when (parts == 0)
                {
                    _fileWrite = false; // 막혔다 — 이 연결 동안은 명령 방식으로
                    source.Position = start;
                    return 0;
                }

                parts++;
                sent += read;
                progress?.Report(sent);
            }
        }
        catch when (parts > 0)
        {
            await CleanupAsync(_dialect.RemoveParts(temp)).ConfigureAwait(false); // 중간에 멈췄다 — 쓴 조각을 치운다
            throw;
        }

        return parts;
    }

    /// <summary>남은 조각 지우기 — 취소된 뒤에도, 실패는 무시한다.</summary>
    private async Task CleanupAsync(IReadOnlyList<string> command)
    {
        try
        {
            await RunAsync(command, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GuestFileException or ProxmoxApiException or TimeoutException
                                       or HttpRequestException)
        {
            // 게스트에 .pvc-up.*.p?????? 이 남을 수 있다
        }
    }

    /// <summary>
    ///     작은 파일은 에이전트 파일 API 로 임시 파일을 한 번에 쓴다(자르기·이어 붙이기 명령 없이). 막혔거나 권한이
    ///     없으면 false(명령으로) — 이 연결 동안 다시 쓰지 않는다. 원본을 되돌려 명령 방식이 처음부터 읽게 한다.
    /// </summary>
    private async Task<bool> TryFileWriteAsync(string temp, Stream source, long length, CancellationToken ct)
    {
        if (!_fileWrite || !source.CanSeek) return false;

        var start = source.Position;
        var buffer = new byte[length];
        var read = await source.ReadAtLeastAsync(buffer, buffer.Length, false, ct).ConfigureAwait(false);
        try
        {
            await _api.Agent.FileWriteBytesAsync(_node, _vmid, temp, buffer.AsMemory(0, read), ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (ProxmoxApiException)
        {
            _fileWrite = false;
            source.Position = start;
            return false;
        }
    }

    /// <summary>남은 임시 파일 지우기 — 취소된 뒤에도 하도록 새 토큰으로, 실패는 무시한다(다음에 지울 수 있다).</summary>
    private async Task CleanupAsync(string path)
    {
        try
        {
            await RunAsync(_dialect.Remove(path), null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GuestFileException or ProxmoxApiException or TimeoutException
                                       or HttpRequestException)
        {
            // 게스트에 .pvc-up.* 이 남을 수 있다
        }
    }

    public Task CreateDirectoryAsync(string directory, string name, CancellationToken ct = default)
    {
        return RunAsync(_dialect.CreateDirectory(directory, _dialect.CheckName(name)), null, ct);
    }

    public Task RenameAsync(string directory, string name, string newName, CancellationToken ct = default)
    {
        return RunAsync(_dialect.Rename(directory, _dialect.CheckName(name), _dialect.CheckName(newName)), null,
            ct);
    }

    public Task DeleteAsync(string directory, GuestFileEntry entry, CancellationToken ct = default)
    {
        return RunAsync(_dialect.Delete(directory, _dialect.CheckName(entry.Name)), null, ct);
    }

    public string ResolveLink(string directory, GuestFileEntry link) => _dialect.ResolveLink(directory, link);
    public string Parent(string directory) => _dialect.Parent(directory);
    public string Combine(string directory, string name) => _dialect.Combine(directory, name);

    /// <summary>명령 실행 — 0 이 아닌 종료는 게스트가 남긴 오류 문구로 알린다.</summary>
    private async Task<string> RunAsync(IReadOnlyList<string> command, string? input, CancellationToken ct)
    {
        var result = await _exec.RunAsync(command, input, ct).ConfigureAwait(false);
        if (result.ExitCode == 0) return result.Output;

        throw new GuestFileException(result.Error.Length > 0
            ? result.Error
            : Res.T("GuestFiles_Failed", result.ExitCode));
    }
}

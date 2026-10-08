using System.Globalization;
using System.Text;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Terminal;

namespace ProxmoxClient.Core.Files;

/// <summary>
///     CT 파일 시스템(root@pam) — 보이지 않는 노드 셸에서 호스트 쪽으로 CT 루트에 접근한다.
///     실행 중이면 /proc/{pid}/root(CT 의 마운트까지 보인다), 꺼져 있으면 작업 동안만 pct mount 로 붙였다 뗀다.
///     목록은 호스트의 GNU find 로 읽어 CT 안에 도구가 없어도 된다. CT 안의 심볼릭 링크는 CT 루트 기준으로 풀어
///     (호스트 perl) 절대 링크가 호스트 경로로 새지 않게 한다. 올린 파일·폴더의 소유자는 담긴 폴더와 같게 맞춘다 —
///     권한 없는(unprivileged) CT 의 UID 변환도 그대로 맞는다. 명령은 <see cref="ContainerScripts" />.
/// </summary>
public sealed class ContainerFileSystem : IGuestFileSystem
{
    private readonly ProxmoxApiClient _api;
    private readonly Dictionary<long, string> _groups = [];
    private readonly string _node;
    private readonly Dictionary<long, string> _users = [];
    private readonly int _vmid;
    private long _idOffset;
    private readonly SemaphoreSlim _reopen = new(1, 1); // 버려진 셸을 한 번만 새로 열게(동시 호출)
    private HiddenShell _shell;

    private ContainerFileSystem(ProxmoxApiClient api, string node, int vmid, HiddenShell shell)
    {
        _api = api;
        _node = node;
        _vmid = vmid;
        _shell = shell;
    }

    public char Separator => '/';
    public StringComparer NameComparer => StringComparer.Ordinal;
    public string HomePath => "/";
    public bool CanDownloadDirectory => true;

    public void Dispose()
    {
        _shell.Dispose();
    }

    /// <summary>노드 셸을 열고 CT 의 UID 변환·사용자 이름을 읽는다.</summary>
    public static async Task<ContainerFileSystem> OpenAsync(ProxmoxApiClient api, string node, int vmid,
        CancellationToken ct = default)
    {
        var shell = await HiddenShell.OpenAsync(api, node, ContainerScripts.Setup, ct).ConfigureAwait(false);
        var files = new ContainerFileSystem(api, node, vmid, shell);
        try
        {
            await files.LoadNamesAsync(ct).ConfigureAwait(false);
            return files;
        }
        catch
        {
            files.Dispose();
            throw;
        }
    }

    /// <summary>CT 루트의 호스트 UID(= 변환 오프셋)와 /etc/passwd·group — 목록의 소유자 이름에 쓴다.</summary>
    private async Task LoadNamesAsync(CancellationToken ct)
    {
        var fields = Split(await RunAsync(With(ContainerScripts.Names), ct).ConfigureAwait(false));
        _idOffset = fields.Count > 0 && long.TryParse(fields[0].Trim(), out var offset) ? offset : 0;
        if (fields.Count > 1) ReadNames(fields[1], _users);
        if (fields.Count > 2) ReadNames(fields[2], _groups);
    }

    private static void ReadNames(string table, Dictionary<long, string> names)
    {
        foreach (var line in table.Split('\n'))
        {
            var parts = line.Split(':');
            if (parts.Length > 2 && long.TryParse(parts[2], out var id)) names.TryAdd(id, parts[0]);
        }
    }

    public Task<IReadOnlyList<GuestFileEntry>> ListAsync(string directory, CancellationToken ct = default)
    {
        return ListAsync(directory, null, ct);
    }

    /// <summary>목록 — 노드 셸 출력은 흘러 들어오므로 받는 대로 읽어 앞부분부터 알린다.</summary>
    public async Task<IReadOnlyList<GuestFileEntry>> ListAsync(string directory,
        Action<IReadOnlyList<GuestFileEntry>>? partial, CancellationToken ct = default)
    {
        var records = new GuestRecordStream(ParseEntry, partial);
        await RunAsync(With(ContainerScripts.List(directory)), ct, records).ConfigureAwait(false);
        return records.Entries;
    }

    /// <summary>"%y/%s/%T@/%m/%U/%G" + 이름 + 링크 대상 — 소유자·그룹 번호는 CT 안 이름으로.</summary>
    private GuestFileEntry ParseEntry(string meta, string name, string link)
    {
        return GuestListFormat.Entry(meta, name, link, id => IdName(id, _users), id => IdName(id, _groups));
    }

    /// <summary>호스트 UID → CT UID(오프셋을 뺀다) → 이름(없으면 번호).</summary>
    private string IdName(string? hostId, Dictionary<long, string> names)
    {
        if (!long.TryParse(hostId, out var id)) return string.Empty;

        var inner = id - _idOffset;
        return names.TryGetValue(inner, out var name) ? name : inner.ToString(CultureInfo.InvariantCulture);
    }

    public async Task DownloadAsync(string directory, GuestFileEntry entry, Stream destination,
        IProgress<long>? progress, CancellationToken ct = default)
    {
        var path = Combine(directory, GuestPaths.CheckName(entry.Name));
        var op = entry.IsDirectory ? ContainerScripts.TarDirectory(path) : ContainerScripts.ReadFile(path);
        await RunAsync(With(op), ct, destination, progress: progress).ConfigureAwait(false);
    }

    public async Task UploadAsync(string directory, string name, Stream source, long length,
        IProgress<long>? progress, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var script = ContainerScripts.Upload(_vmid, directory, GuestPaths.CheckName(name), length, id);
        await RunAsync(script, ct, input: source, inputLength: length, progress: progress).ConfigureAwait(false);
    }

    public Task CreateDirectoryAsync(string directory, string name, CancellationToken ct = default)
    {
        return RunAsync(With(ContainerScripts.CreateDirectory(directory, GuestPaths.CheckName(name))), ct);
    }

    public Task RenameAsync(string directory, string name, string newName, CancellationToken ct = default)
    {
        var script = ContainerScripts.Rename(directory, GuestPaths.CheckName(name), GuestPaths.CheckName(newName));
        return RunAsync(With(script), ct);
    }

    public Task DeleteAsync(string directory, GuestFileEntry entry, CancellationToken ct = default)
    {
        return RunAsync(With(ContainerScripts.Delete(directory, GuestPaths.CheckName(entry.Name))), ct);
    }

    public string ResolveLink(string directory, GuestFileEntry link)
    {
        return GuestPaths.ResolveLink(directory, link);
    }

    public string Parent(string directory)
    {
        return GuestPaths.Parent(directory);
    }

    public string Combine(string directory, string name)
    {
        return GuestPaths.Combine(directory, name);
    }

    private string With(string op)
    {
        return ContainerScripts.With(_vmid, op);
    }

    private async Task<byte[]> RunAsync(string script, CancellationToken ct)
    {
        using var output = new MemoryStream();
        await RunAsync(script, ct, output).ConfigureAwait(false);
        return output.ToArray();
    }

    /// <summary>명령 실행 — 실패(0 이 아닌 종료)는 셸이 남긴 오류 문구로 알린다. 버려진 셸은 새로 연다.</summary>
    private async Task RunAsync(string script, CancellationToken ct, Stream? output = null, Stream? input = null,
        long inputLength = 0, IProgress<long>? progress = null)
    {
        var shell = await ShellAsync(ct).ConfigureAwait(false);
        var result = await shell.RunAsync(script, output, input, inputLength, progress, ct).ConfigureAwait(false);
        if (result.ExitCode == 0) return;

        var message = result.ExitCode == 97
            ? Localization.Res.T("GuestFiles_MountFailed", _vmid, result.Error)
            : result.ExitCode == 98
                ? Localization.Res.T("GuestFiles_MountBusy", _vmid)
            : result.Error.Length > 0
                ? result.Error
                : Localization.Res.T("GuestFiles_Failed", result.ExitCode);
        throw new GuestFileException(message);
    }

    /// <summary>쓸 수 있는 셸 — 버려졌으면 새로 연다(여럿이 동시에 보아도 한 번만).</summary>
    private async Task<HiddenShell> ShellAsync(CancellationToken ct)
    {
        if (!_shell.IsBroken) return _shell;

        await _reopen.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_shell.IsBroken)
            {
                _shell.Dispose();
                _shell = await HiddenShell.OpenAsync(_api, _node, ContainerScripts.Setup, ct).ConfigureAwait(false);
            }

            return _shell;
        }
        finally
        {
            _reopen.Release();
        }
    }

    private static List<string> Split(byte[] data)
    {
        var fields = Encoding.UTF8.GetString(data).Split('\0').ToList();
        if (fields.Count > 0 && fields[^1].Length == 0) fields.RemoveAt(fields.Count - 1);
        return fields;
    }
}

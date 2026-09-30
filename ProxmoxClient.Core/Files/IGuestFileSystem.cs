namespace ProxmoxClient.Core.Files;

/// <summary>
///     게스트 파일 시스템(CT — 보이지 않는 노드 셸, VM — 게스트 에이전트) — 파일 패널이 이 모양만 보고 동작한다.
///     경로는 게스트 기준 절대 경로이고, 이름은 경로 구분자를 담지 않는 한 항목 이름이다.
///     모든 작업은 한 번에 하나씩 순서대로 처리된다(구현이 줄 세운다).
/// </summary>
public interface IGuestFileSystem : IDisposable
{
    /// <summary>경로 구분자('/' 또는 Windows '\').</summary>
    char Separator { get; }

    /// <summary>
    ///     이름 비교 — Windows 게스트는 대소문자를 가리지 않고(readme.md 와 README.md 는 같은 파일),
    ///     Linux 등은 가린다. 덮어쓰기 확인·캐시 등 게스트 이름을 견줄 때 쓴다.
    /// </summary>
    StringComparer NameComparer { get; }

    /// <summary>처음 보여 줄 폴더.</summary>
    string HomePath { get; }

    /// <summary>폴더 안의 항목(., .. 제외).</summary>
    Task<IReadOnlyList<GuestFileEntry>> ListAsync(string directory, CancellationToken ct = default);

    /// <summary>
    ///     폴더 안의 항목 — 받는 도중 지금까지 받은 목록(누적)을 partial 로 알린다(큰 폴더를 앞부분부터 보이게).
    ///     partial 은 아무 스레드에서나 불린다. 나눠 받지 못하는 구현은 끝에 한 번에 돌려준다.
    /// </summary>
    Task<IReadOnlyList<GuestFileEntry>> ListAsync(string directory, Action<IReadOnlyList<GuestFileEntry>>? partial,
        CancellationToken ct = default) => ListAsync(directory, ct);

    /// <summary>
    ///     파일 하나를 받아 destination 에 쓴다 — 폴더면 tar 로 묶어 받는다(<see cref="CanDownloadDirectory" />).
    ///     progress 는 받은 바이트 수.
    /// </summary>
    Task DownloadAsync(string directory, GuestFileEntry entry, Stream destination, IProgress<long>? progress,
        CancellationToken ct = default);

    /// <summary>폴더를 tar 로 받을 수 있다.</summary>
    bool CanDownloadDirectory { get; }

    /// <summary>source 의 내용을 directory/name 으로 올린다(있으면 덮어쓴다). progress 는 보낸 바이트 수.</summary>
    Task UploadAsync(string directory, string name, Stream source, long length, IProgress<long>? progress,
        CancellationToken ct = default);

    Task CreateDirectoryAsync(string directory, string name, CancellationToken ct = default);

    Task RenameAsync(string directory, string name, string newName, CancellationToken ct = default);

    /// <summary>항목을 지운다(폴더면 안의 내용까지).</summary>
    Task DeleteAsync(string directory, GuestFileEntry entry, CancellationToken ct = default);

    /// <summary>링크가 가리키는 게스트 경로(상대 경로면 directory 기준으로 풀어서).</summary>
    string ResolveLink(string directory, GuestFileEntry link);

    /// <summary>directory 의 부모 폴더(맨 위면 그대로).</summary>
    string Parent(string directory);

    /// <summary>directory 안의 name.</summary>
    string Combine(string directory, string name);
}

/// <summary>게스트에서 실패한 파일 작업 — 게스트(셸·에이전트)가 알려 준 오류를 그대로 담는다.</summary>
public class GuestFileException(string message) : Exception(message);

/// <summary>게스트 OS 갈래 — 에이전트 설정을 푸는 방법이 다르다.</summary>
public enum GuestOsFamily
{
    Windows,

    /// <summary>RHEL·CentOS·Rocky·Alma·Oracle Linux·Fedora 등 — 에이전트 기본 설정이 명령 실행을 막고 SELinux 를 쓴다.</summary>
    RedHat,

    /// <summary>Debian·Ubuntu 등 그 밖의 Linux(알 수 없을 때도).</summary>
    Linux
}

/// <summary>
///     게스트 에이전트가 명령 실행(guest-exec)을 막아 두었다 — 파일 창이 푸는 명령을 안내한다.
///     ScriptCommand 는 해제 스크립트를 게스트에 써 두었을 때 그것을 실행하는 한 줄(못 썼으면 null — 전체 명령을 보인다).
/// </summary>
public sealed class AgentExecBlockedException(GuestOsFamily family, string? scriptCommand = null)
    : GuestFileException(Localization.Res.T("AgentFiles_NoExec"))
{
    public GuestOsFamily Family { get; } = family;
    public string? ScriptCommand { get; } = scriptCommand;
}

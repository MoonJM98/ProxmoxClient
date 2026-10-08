using System.Security.Cryptography;
using System.Text;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.Core.Files;

/// <summary>
///     게스트 에이전트가 명령 실행(guest-exec)을 막아 두었을 때 푸는 명령 — 파일 창이 사용자에게 보인다.
///     에이전트 파일 쓰기가 열려 있으면 같은 내용을 스크립트로 게스트에 써 두고 한 줄로 실행하게 한다(실행은 사용자가).
/// </summary>
public static class AgentUnblock
{
    /// <summary>
    ///     Linux(/etc/sysconfig/qemu-ga) — 허용 목록(RHEL 9: FILTER_RPC_ARGS="--allow-rpcs=…")이면 파일 기능에 필요한
    ///     명령만 끝에 더하고(다른 명령은 그대로 막힌 채), 차단 목록(RHEL 8: --block-rpcs, RHEL 7: BLACKLIST_RPC)이면
    ///     그 줄을 주석으로 돌린다. 파일이 없으면(Debian 등) 넘어가고 에이전트를 다시 시작.
    /// </summary>
    public const string Linux =
        "sed -i -E -e '/^FILTER_RPC_ARGS=.*--allow-rpcs=/ s/\"[[:space:]]*$/,guest-exec,guest-exec-status,"
        + "guest-file-open,guest-file-close,guest-file-read,guest-file-write,guest-file-seek,guest-file-flush\"/' "
        + "-e '/^FILTER_RPC_ARGS=.*--block-rpcs=/ s/^/#/' -e 's/^BLACKLIST_RPC=/#&/' "
        + "/etc/sysconfig/qemu-ga 2>/dev/null\n"
        + "systemctl restart qemu-guest-agent";

    /// <summary>SELinux(RHEL 계열) — 에이전트로 실행한 명령이 파일을 읽고 쓰게.</summary>
    public const string Selinux = "setsebool -P virt_qemu_ga_run_unconfined on";

    /// <summary>Windows(관리자 PowerShell) — 서비스 실행 인자에서 -b/--block-rpcs·-a/--allow-rpcs 를 지우고 다시 시작.</summary>
    public const string Windows =
        "$k = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\QEMU-GA'\n"
        + "(Get-ItemProperty $k).ImagePath\n"
        + "Set-ItemProperty $k ImagePath ((Get-ItemProperty $k).ImagePath "
        + "-replace '\\s+(-b|--block-rpcs|-a|--allow-rpcs)(=|\\s+)\\S+', '')\n"
        + "Restart-Service QEMU-GA";

    /// <summary>게스트 OS 에 맞는 전체 명령(사용자가 붙여 넣어 실행).</summary>
    public static string Commands(GuestOsFamily family) => family switch
    {
        GuestOsFamily.Windows => Windows,
        GuestOsFamily.RedHat => Linux + "\n" + Selinux,
        _ => Linux
    };

    /// <summary>
    ///     해제 스크립트를 게스트에 써 본다 — 에이전트가 파일 쓰기를 허용하면(목록에 있으면) 차례로 시도하고, 써지면 그것을
    ///     실행하는 한 줄 명령을, 막혔거나 모두 실패하면 null(전체 명령을 보이게). 이름은 무작위 — 다른 사용자가 같은 이름의
    ///     링크를 미리 만들어 root 로 쓰는 에이전트가 엉뚱한 파일을 덮어쓰지 않게. Linux 는 root 만 들어가는 /root 먼저.
    /// </summary>
    internal static async Task<string?> TryWriteScriptAsync(ProxmoxApiClient api, string node, int vmid,
        GuestOsFamily family, IReadOnlySet<string> agentCommands, CancellationToken ct)
    {
        if (!agentCommands.Contains("guest-file-open") || !agentCommands.Contains("guest-file-write")
                                                       || !agentCommands.Contains("guest-file-close"))
            return null;

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        foreach (var (path, content, run) in Scripts(family, id))
            try
            {
                await api.Agent.FileWriteBytesAsync(node, vmid, path, content, ct).ConfigureAwait(false);
                return run;
            }
            catch (ProxmoxApiException)
            {
                // 이 경로는 못 쓴다(없는 폴더·SELinux 등) — 다음 경로로
            }

        return null;
    }

    /// <summary>쓸 경로·내용·실행 명령 — 스크립트는 끝나면 스스로 지운다.</summary>
    internal static IEnumerable<(string Path, byte[] Content, string Run)> Scripts(GuestOsFamily family, string id)
    {
        if (family == GuestOsFamily.Windows)
        {
            var ps = $@"C:\Windows\Temp\pvc-agent-unblock-{id}.ps1";
            var body = Windows.Replace("\n", "\r\n") + "\r\nRemove-Item -LiteralPath $PSCommandPath -Force\r\n";
            // PowerShell 5.1 이 한글 등을 바르게 읽도록 BOM 을 붙인다
            yield return (ps, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(body)],
                $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{ps}\"");
            yield break;
        }

        var script = Encoding.UTF8.GetBytes("#!/bin/sh\n" + Commands(family) + "\nrm -f -- \"$0\"\n");
        foreach (var folder in new[] { "/root", "/tmp" })
        {
            var sh = $"{folder}/pvc-agent-unblock-{id}.sh";
            yield return (sh, script, $"sh {sh}");
        }
    }
}

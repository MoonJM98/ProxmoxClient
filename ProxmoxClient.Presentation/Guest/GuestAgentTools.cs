using System.Text;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Presentation.Shared;
using static ProxmoxClient.Presentation.Shared.ActionHelpers;
using static ProxmoxClient.Presentation.Shared.FormHelpers;

namespace ProxmoxClient.Presentation.Guest;

/// <summary>
///     게스트 에이전트로 게스트 안 다루기 — 명령 실행, 파일 읽기·쓰기, 파일 시스템 동결. 게스트를 root 로 다루는 것과
///     같으므로 권한이 있는 사용자에게만 버튼을 두고, 실행·쓰기는 내용을 보여 준 뒤 한 번 더 확인한다.
///     동결은 작업 버튼의 확인 문구(Confirm)로 묻는다.
/// </summary>
internal static class GuestAgentTools
{
    /// <summary>명령이 끝나기를 기다리는 최대 시간 — 넘으면 게스트에서 계속 돌고, 창에는 pid 만 알린다.</summary>
    private static readonly TimeSpan ExecWait = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan ExecPoll = TimeSpan.FromSeconds(1);

    /// <summary>서버가 한 번에 받는 파일 쓰기 크기(60 KiB).</summary>
    private const int MaxWriteBytes = 61440;

    public static IEnumerable<TableAction> Create(ProxmoxApiClient api, PveResource guest,
        PermissionsInfo permissions)
    {
        // 9.x 는 VM.GuestAgent.* 세분 권한, 그 전은 VM.Monitor 하나로 모두 허용했다
        const string Legacy = "VM.Monitor";
        const string Unrestricted = "VM.GuestAgent.Unrestricted";
        if (permissions.HasAny(Unrestricted, Legacy))
            yield return new TableAction
            {
                LabelKey = "AgentTool_Exec", IconKey = "IconTerminal",
                Run = (_, owner) => ExecAsync(api, guest, owner)
            };
        if (permissions.HasAny("VM.GuestAgent.FileRead", Unrestricted, Legacy))
            yield return new TableAction
            {
                LabelKey = "AgentTool_Read", IconKey = "IconList",
                Run = (_, owner) => ReadAsync(api, guest, owner)
            };
        if (permissions.HasAny("VM.GuestAgent.FileWrite", Unrestricted, Legacy))
            yield return new TableAction
            {
                LabelKey = "AgentTool_Write", IconKey = "IconSave",
                Run = (_, owner) => WriteAsync(api, guest, owner)
            };
        if (permissions.HasAny("VM.GuestAgent.FileSystemMgmt", Unrestricted, Legacy))
        {
            yield return Freeze(api, guest, "freeze", "AgentTool_Freeze", "IconPause",
                UiText.T("AgentTool_FreezeConfirm", guest.VmId));
            yield return Freeze(api, guest, "thaw", "AgentTool_Thaw", "IconPlay", null);
        }

        if (permissions.HasAny("VM.GuestAgent.Audit", "VM.GuestAgent.FileSystemMgmt", Unrestricted, Legacy))
            yield return Freeze(api, guest, "status", "AgentTool_FreezeStatus", "IconSearch", null);
    }

    private static TableAction Freeze(ProxmoxApiClient api, PveResource guest, string action, string labelKey,
        string iconKey, string? confirm)
    {
        return new TableAction
        {
            LabelKey = labelKey, IconKey = iconKey, Confirm = confirm is null ? null : _ => confirm,
            Run = async (_, _) => UiText.T("AgentTool_FreezeResult", UiText.T(labelKey),
                await api.Agent.FsFreezeAsync(guest.Node, guest.VmId, action))
        };
    }

    // ------------------------------------------------------------ 명령 실행

    private static async Task<string?> ExecAsync(ProxmoxApiClient api, PveResource guest, object? owner)
    {
        var fields = new List<FormField>
        {
            new() { Key = "command", LabelKey = "AgentTool_Command", Required = true, Trim = true,
                Hint = UiText.T("AgentTool_CommandHint") },
            new() { Key = "input", LabelKey = "AgentTool_Input", Kind = FormFieldKind.Multiline,
                CanLoadFile = false, Trim = false, Advanced = true }
        };
        if (await AppUi.FormAsync(owner, UiText.T("AgentTool_ExecTitle", guest.VmId), fields) is not { } values)
            return null;

        var command = GuestAgentApi.SplitCommandLine(values["command"]);
        if (command is null) return UiText.T("AgentTool_UnbalancedQuote");
        if (command.Count == 0) return null;
        var shown = string.Join(" ", command.Select(Quote));
        if (values["input"].Length > 0) shown += "\n" + UiText.T("AgentTool_WithInput", values["input"].Length);
        if (!await AppUi.ConfirmAsync(owner, UiText.T("AgentTool_ExecConfirm", guest.VmId, shown),
                UiText.T("TableTab_ConfirmTitle")))
            return null;

        // 위에서 확인을 받았으므로 이 실행 요청은 다시 묻지 않는다
        using var flow = AppUi.BeginWrite(UiText.T("AgentTool_Exec"), confirmed: true);
        var pid = await api.Agent.ExecAsync(guest.Node, guest.VmId, command, values["input"]);
        var deadline = DateTime.UtcNow + ExecWait;
        while (DateTime.UtcNow < deadline)
        {
            var status = await api.Agent.ExecStatusAsync(guest.Node, guest.VmId, pid);
            if (Value(status, "exited") is "1" or "true")
            {
                AppUi.ShowText(owner, UiText.T("AgentTool_ExecResultTitle", command[0]), Report(status));
                return UiText.T("AgentTool_ExecDone", Value(status, "exitcode"));
            }

            await Task.Delay(ExecPoll);
        }

        return UiText.T("AgentTool_ExecStillRunning", pid);
    }

    /// <summary>종료 코드·표준 출력·오류 출력을 한 글로(잘린 출력은 표시).</summary>
    private static string Report(IReadOnlyDictionary<string, string> status)
    {
        var text = new StringBuilder();
        text.AppendLine(UiText.T("AgentTool_ExitCode", Value(status, "exitcode"),
            Value(status, "signal") is { Length: > 0 } signal ? $" (signal {signal})" : string.Empty));
        Append(text, "AgentTool_Stdout", Value(status, "out-data"), Value(status, "out-truncated"));
        Append(text, "AgentTool_Stderr", Value(status, "err-data"), Value(status, "err-truncated"));
        return text.ToString();
    }

    private static void Append(StringBuilder text, string headerKey, string data, string truncated)
    {
        if (data.Length == 0) return;

        text.AppendLine().AppendLine($"[{UiText.T(headerKey)}]").AppendLine(data.TrimEnd());
        if (truncated is "1" or "true") text.AppendLine(UiText.T("AgentTool_Truncated"));
    }

    private static string Quote(string part)
    {
        return part.Length == 0 || part.Any(char.IsWhiteSpace) ? $"\"{part}\"" : part;
    }

    // ------------------------------------------------------------ 파일

    private static async Task<string?> ReadAsync(ProxmoxApiClient api, PveResource guest, object? owner)
    {
        var fields = new List<FormField>
        {
            new() { Key = "path", LabelKey = "Table_Path", Required = true, Trim = true,
                Hint = UiText.T("AgentTool_PathHint") }
        };
        if (await AppUi.FormAsync(owner, UiText.T("AgentTool_ReadTitle", guest.VmId), fields) is not { } values)
            return null;

        var result = await api.Agent.FileReadAsync(guest.Node, guest.VmId, values["path"]);
        var content = Value(result, "content");
        if (Value(result, "truncated") is "1" or "true") content += "\n\n" + UiText.T("AgentTool_Truncated");
        AppUi.ShowText(owner, values["path"], content);
        return null;
    }

    private static async Task<string?> WriteAsync(ProxmoxApiClient api, PveResource guest, object? owner)
    {
        var fields = new List<FormField>
        {
            new() { Key = "path", LabelKey = "Table_Path", Required = true, Trim = true,
                Hint = UiText.T("AgentTool_PathHint") },
            new() { Key = "content", LabelKey = "AgentTool_Content", Kind = FormFieldKind.Multiline,
                Trim = false, Hint = UiText.T("AgentTool_WriteHint") }
        };
        if (await AppUi.FormAsync(owner, UiText.T("AgentTool_WriteTitle", guest.VmId), fields, input =>
                Encoding.UTF8.GetByteCount(input["content"]) > MaxWriteBytes
                    ? UiText.T("AgentTool_WriteTooLarge", MaxWriteBytes / 1024)
                    : null) is not { } values)
            return null;

        if (!await AppUi.ConfirmAsync(owner, UiText.T("AgentTool_WriteConfirm", guest.VmId, values["path"]),
                UiText.T("TableTab_ConfirmTitle")))
            return null;

        using var flow = AppUi.BeginWrite(UiText.T("AgentTool_Write"), confirmed: true);
        await api.Agent.FileWriteAsync(guest.Node, guest.VmId, values["path"], values["content"]);
        return UiText.T("AgentTool_Written", values["path"]);
    }
}

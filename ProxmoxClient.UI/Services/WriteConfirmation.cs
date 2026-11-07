using Avalonia.Threading;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Services;

/// <summary>
///     "서버 상태를 바꾸는 요청은 무조건 사용자 확인을 거친다"를 통신 계층에서 보장한다.
///     로그인할 때 <see cref="ProxmoxApiClient.WriteGuard" /> 에 <see cref="GuardAsync" /> 를 건다 —
///     GET 이 아닌 모든 요청은 보내기 직전에 여기를 지나고, 확인을 받지 않았으면 대화상자로 묻는다.
///     화면이 먼저 물어본 작업은 <see cref="Begin" />(confirmed: true) 안에서 실행해 두 번 묻지 않게 한다.
///     한 작업 흐름(Begin 범위) 안에서 한 번 확인하면 이어지는 요청은 다시 묻지 않는다.
/// </summary>
public static class WriteConfirmation
{
    private static readonly AsyncLocal<Flow?> CurrentFlow = new();

    /// <summary>
    ///     서버 상태를 바꾸지 않는 POST — 콘솔 연결 표 발급만 한다(확인 없이 보낸다). rdpproxy 는 RDP 콘솔.
    /// </summary>
    private static readonly string[] NonChangingSuffixes = ["/vncproxy", "/termproxy", "/spiceproxy", "/rdpproxy"];

    /// <summary>작업 흐름을 시작한다. 반환값을 Dispose 하면 이전 흐름으로 돌아간다.</summary>
    /// <param name="description">확인 창에 보일 작업 이름(예: "사용자 추가").</param>
    /// <param name="confirmed">화면이 이미 확인을 받았으면 true.</param>
    public static IDisposable Begin(string? description, bool confirmed = false)
    {
        var previous = CurrentFlow.Value;
        CurrentFlow.Value = new Flow(description) { Confirmed = confirmed };
        return new Restore(previous);
    }

    public static async Task<bool> GuardAsync(WriteRequest request, CancellationToken ct)
    {
        var path = request.Path.Split('?')[0].TrimEnd('/');
        if (request.Method == "POST" && NonChangingSuffixes.Any(s => path.EndsWith(s, StringComparison.Ordinal)))
            return true;

        var flow = CurrentFlow.Value;
        if (flow is { Confirmed: true }) return true;

        var message = flow?.Description is { Length: > 0 } description
            ? L.T("Write_ConfirmTextNamed", description, request.Method, path)
            : L.T("Write_ConfirmText", request.Method, path);
        var destructive = request.Method == "DELETE";

        Task<bool> Ask()
        {
            return AppServices.Dialogs.ConfirmAsync(L.T("Write_ConfirmTitle"), message, L.T("Write_Apply"),
                destructive);
        }

        var ok = Dispatcher.UIThread.CheckAccess()
            ? await Ask()
            : await Dispatcher.UIThread.InvokeAsync(Ask);
        if (ok && flow is not null) flow.Confirmed = true;
        return ok;
    }

    private sealed class Flow(string? description)
    {
        public string? Description { get; } = description;

        public bool Confirmed { get; set; }
    }

    private sealed class Restore(Flow? previous) : IDisposable
    {
        public void Dispose()
        {
            CurrentFlow.Value = previous;
        }
    }
}

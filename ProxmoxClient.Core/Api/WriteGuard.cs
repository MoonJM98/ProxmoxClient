namespace ProxmoxClient.Core.Api;

/// <summary>보내려는 쓰기 요청 — 메서드(POST·PUT·DELETE)와 api2/json 기준 상대 경로(쿼리 포함).</summary>
public sealed record WriteRequest(string Method, string Path);

/// <summary><see cref="ProxmoxApiClient.WriteGuard" /> 가 쓰기 요청을 막았다(사용자가 확인하지 않음).</summary>
public sealed class WriteCancelledException(string method, string path)
    : OperationCanceledException($"{method} {path} was not confirmed.")
{
    public string Method { get; } = method;

    public string Path { get; } = path;
}

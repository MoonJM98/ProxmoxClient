namespace ProxmoxClient.Core.Api.Versioning;

/// <summary>
///     이 메서드가 부르는 PVE API 엔드포인트와 그 엔드포인트가 들어온 버전.
///     경로는 API 문서 형식(앞 '/', 이름 자리는 {param})으로 적는다 — 검사 도구가 API 스키마와 맞춰 본다.
///     한 메서드가 서버 버전에 따라 다른 엔드포인트를 쓰면 여러 번 붙인다(예: 8.1 전의 옛 경로).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class PveApiAttribute(string method, string path) : Attribute
{
    public string Method { get; } = method;

    public string Path { get; } = path;

    /// <summary>엔드포인트가 들어온 버전("8.1"). 비우면 지원 범위 전체(7.0 이상).</summary>
    public string Since { get; init; } = "7.0";

    /// <summary>이 버전부터 쓰지 않는 옛 경로(새 버전에선 다른 엔드포인트로 대신한다).</summary>
    public string? Until { get; init; }

    /// <summary>
    ///     지금 API 스키마에는 없는(이름이 바뀌어 사라진) 옛 경로 — 검사 도구는 스키마 대조 대신 Until 이 있는지만 본다.
    /// </summary>
    public bool Legacy { get; init; }

    /// <summary>
    ///     아직 정식 PVE 에 없는(개발 목록에 올라온 패치 등) 엔드포인트 — 검사 도구는 스키마 대조를 건너뛴다.
    ///     버전으로 켜고 끌 수 없으므로 부르는 쪽이 서버 상태(예: VM 설정)로 지원 여부를 먼저 확인한다.
    /// </summary>
    public bool Experimental { get; init; }

    public PveApiVersion SinceVersion => PveApiVersion.Parse(Since);

    public PveApiVersion? UntilVersion => Until is null ? null : PveApiVersion.Parse(Until);
}

/// <summary>
///     엔드포인트는 예전부터 있었지만 이 메서드가 보내는 파라미터가 나중에 들어온 경우 — 파라미터 이름과 버전.
///     낮은 버전 서버에는 그 파라미터를 보내지 않거나 기능을 숨긴다.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class PveParamAttribute(string name, string since) : Attribute
{
    public string Name { get; } = name;

    public string Since { get; } = since;

    public PveApiVersion SinceVersion => PveApiVersion.Parse(Since);
}

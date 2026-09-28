namespace ProxmoxClient.Core.Api.Versioning;

/// <summary>
///     화면 요소가 쓰는 API 기능 — 지금 서버에서 쓸 수 있는지(<see cref="IsAvailable" />), 그 요청이 어떤 파라미터를
///     받는지(<see cref="Accepts" />). 영역 API 가 [PveApi]·[PveParam] 표시로 만들어 준다
///     (예: <c>api.Users.Feature(nameof(UsersApi.UnlockTfaAsync))</c>).
///     화면 공용 부품(표 버튼·탭·입력 창·옵션 목록)이 이것만 보고 보일지를 정한다 — 화면마다 버전을 묻지 않는다.
/// </summary>
public sealed class ApiFeature
{
    private readonly Func<bool> _available;
    private readonly Func<string, bool> _accepts;

    public ApiFeature(Func<bool> available, Func<string, bool>? accepts = null)
    {
        _available = available;
        _accepts = accepts ?? (_ => true);
    }

    /// <summary>늘 쓸 수 있는 기능(지원 범위 7.0 부터 있던 것).</summary>
    public static ApiFeature Always { get; } = new(() => true);

    /// <summary>지금 서버에서 쓸 수 있는지. 서버 버전을 모르면 true.</summary>
    public bool IsAvailable => _available();

    /// <summary>이 요청이 그 이름의 파라미터(입력 칸·옵션 키)를 지금 서버에서 받는지. 표시 없는 이름은 true.</summary>
    public bool Accepts(string key)
    {
        return _accepts(key);
    }
}

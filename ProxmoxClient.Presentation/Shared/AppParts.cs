using ProxmoxClient.Core.Api;

namespace ProxmoxClient.Presentation.Shared;

/// <summary>
///     공용 화면 안에서 두 앱이 아직 다르게 만드는 조각 — 공용 코드는 그 자리에 앱이 넣어 준 것을 쓴다.
///     (기능·확인 흐름이 앱마다 달라 합치면 한쪽 동작이 바뀌는 것만 여기에 둔다.)
/// </summary>
public interface IAppParts
{
    /// <summary>
    ///     읽을 때 받은 digest 를 쓰기에 함께 보내, 그 사이 다른 곳에서 바뀐 설정을 덮어쓰지 않는다(<see cref="Guest.Tabs.DigestGuard" />).
    ///     Avalonia 만 보낸다 — WPF 는 아직 보내지 않는다.
    /// </summary>
    bool SendsDigest { get; }

    /// <summary>게스트 파일 연결의 비밀번호·암호 보관.</summary>
    IGuestSecretStore GuestSecrets { get; }

    /// <summary>VNet 방화벽 화면 — 방화벽 옵션 화면이 앱마다 다르다(WPF OptionsTab · Avalonia 표).</summary>
    ViewSpec VnetFirewall(ProxmoxApiClient api, string vnet, bool canEdit);

    /// <summary>노드 네트워크 표의 작업 버튼 — Avalonia 는 VLAN·IPv6 칸과 적용 전 바뀐 내용 확인이 더 있다.</summary>
    IReadOnlyList<TableAction> NetworkActions(ProxmoxApiClient api, string node);

    /// <summary>
    ///     2단계 인증 등록 단추(TOTP·Yubico·복구 키) — QR 코드 창이 앱마다 다르고(WPF 모달 창 · Avalonia 화면),
    ///     로그인 2단계 인증 작업과 함께 바뀌고 있어 아직 옮기지 않았다.
    /// </summary>
    IReadOnlyList<TableAction> TfaRegistration(ProxmoxApiClient api);

    /// <summary>
    ///     디스크를 늘릴 크기(GiB) → 서버 형식 "+…G", 받을 수 없는 값이면 null. Avalonia 는 소수점 숫자만 받고
    ///     문화권과 무관하게 점으로 보낸다. WPF 는 실수(1e3 등)도 받고 입력 그대로 보낸다.
    /// </summary>
    string? ResizeSize(string text, int maxGib);

    /// <summary>저장소 사용량 그래프 탭 — 그래프 컨트롤이 앱마다 다르다.</summary>
    ViewSpec StorageGraph(ProxmoxApiClient api, string node, string storage);

    /// <summary>PBS 백업 속 파일 복원 단추 — 파일 탐색 화면이 앱마다 다르다.</summary>
    TableAction FileRestore(ProxmoxApiClient api, string node, string storage);

    /// <summary>
    ///     ISO·CT 템플릿 올리기 — 파일 고르기·진행 표시가 앱마다 다르다(WPF 파일 열기 창 · Avalonia 선택기 스트림과
    ///     체크섬 입력). 결과 문구(취소면 null 또는 취소 문구).
    /// </summary>
    Task<string?> UploadAsync(ProxmoxApiClient api, string node, string storage, string content, object? owner);

    /// <summary>Ceph 설치(노드 셸에서 pveceph install) — Avalonia 는 셸을 열기 전에 확인을 받는다.</summary>
    TableAction CephInstall(ProxmoxApiClient api, string node);

    /// <summary>apt 업그레이드(노드 셸에서 apt dist-upgrade) — Avalonia 는 셸을 열기 전에 확인을 받는다.</summary>
    TableAction AptUpgrade(ProxmoxApiClient api, string node);
}

/// <summary>앱이 시작할 때 넣는 <see cref="IAppParts" />.</summary>
public static class AppParts
{
    private static IAppParts? _current;

    public static IAppParts Current
    {
        get => _current ?? throw new InvalidOperationException("AppParts.Current 를 앱 시작 때 넣어야 한다");
        set => _current = value;
    }
}

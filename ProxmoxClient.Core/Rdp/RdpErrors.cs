using Devolutions.IronRdp;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Rdp;

/// <summary>
///     IronRDP 오류를 사용자에게 보일 글로 바꾼다. CredSSP(NLA) 로그인 실패는 서버가 NTSTATUS 로 알려 주는데
///     IronRDP 는 영어 문장("CredSSP server returned an error status; status is STATUS_LOGON_FAILURE …")으로만 준다 —
///     흔한 경우는 무엇이 문제인지 알 수 있게 바꾸고, 나머지는 원문을 붙인다.
/// </summary>
public static class RdpErrors
{
    public static string Describe(IronRdpErrorKind kind, string detail)
    {
        // 서버가 일회성 계정을 거부했다(비밀번호·사용자 불일치) — 앱이 바꿀 수 있는 값이 아니다
        if (detail.Contains("STATUS_LOGON_FAILURE", StringComparison.Ordinal) ||
            detail.Contains("0xc000006d", StringComparison.OrdinalIgnoreCase))
            return Res.T("Rdp_LogonFailed");

        // 계정은 맞지만 원격 로그온 권한이 없다(원격 데스크톱 사용자 그룹·정책)
        if (detail.Contains("STATUS_LOGON_TYPE_NOT_GRANTED", StringComparison.Ordinal) ||
            detail.Contains("0xc000015b", StringComparison.OrdinalIgnoreCase))
            return Res.T("Rdp_LogonNotGranted");

        return kind == IronRdpErrorKind.CredsspError || detail.Contains("CredSSP", StringComparison.Ordinal)
            ? Res.T("Rdp_CredsspFailed", detail)
            : Res.T("Rdp_Failed", detail);
    }
}

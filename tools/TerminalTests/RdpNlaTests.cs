using Devolutions.IronRdp;
using ProxmoxClient.Core.Rdp;

/// <summary>NLA(CredSSP) 서버(FreeRDP 그림자 서버)로 시험하며 찾은 문제 — 로그인 실패를 알아볼 수 있는 글로.</summary>
public static class RdpNlaTests
{
    /// <summary>CredSSP 로그인 실패(NTSTATUS)는 IronRDP 원문 대신 무엇이 문제인지 알리고, 그 밖의 오류는 원문을 붙인다.</summary>
    public static void CredsspErrorsAreDescribed(Checker c)
    {
        // FreeRDP 그림자 서버에 틀린 비밀번호로 붙었을 때 IronRDP 가 준 그대로
        const string logon =
            "InvalidToken: CredSSP server returned an error status; status is STATUS_LOGON_FAILURE [0xc000006d]";
        var logonText = RdpErrors.Describe(IronRdpErrorKind.CredsspError, logon);
        c.Is(!logonText.Contains("STATUS_") && !logonText.Contains("CredSSP server"), "로그인 실패는 원문 대신 설명", logonText);
        c.Equal(logonText, RdpErrors.Describe(IronRdpErrorKind.Generic, "status 0xC000006D"), "숫자만 와도 같은 글");

        var grant = RdpErrors.Describe(IronRdpErrorKind.CredsspError,
            "status is STATUS_LOGON_TYPE_NOT_GRANTED [0xc000015b]");
        c.Is(grant != logonText && !grant.Contains("STATUS_"), "원격 로그온 권한 없음은 따로", grant);

        const string other = "CredSSP server returned an error status; status is STATUS_ACCOUNT_RESTRICTION";
        var otherText = RdpErrors.Describe(IronRdpErrorKind.CredsspError, other);
        var plain = RdpErrors.Describe(IronRdpErrorKind.DecodeError, "decode error");
        c.Is(otherText.Contains(other) && otherText != other, "그 밖의 CredSSP 는 원문을 붙인다", otherText);
        c.Is(plain.Contains("decode error") && !plain.Contains("NLA"), "CredSSP 아닌 오류는 일반 RDP 오류", plain);
    }
}

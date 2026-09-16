using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Models;

/// <summary>GET /access/domains 항목 — 인증 영역(로그인 유형).</summary>
public sealed class PveAuthDomain
{
    /// <summary>영역 ID (예: "pam", "pve", "ldap", "openid").</summary>
    public string Realm { get; init; } = string.Empty;

    /// <summary>영역 유형 (pam|pve|ldap|openid|ad|saml).</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>관리자가 설정한 설명(있으면).</summary>
    public string Comment { get; init; } = string.Empty;

    /// <summary>이 영역이 2단계 인증(TFA)을 요구하는지.</summary>
    public bool RequiresTfa { get; init; }

    /// <summary>서버가 지정한 기본 영역(default=1)인지.</summary>
    public bool IsDefault { get; init; }

    /// <summary>표시 이름 — Web UI 규칙: 관리자 설명이 있으면 그것, 없으면 realm 이름(MOON/pam/pve).</summary>
    public string DisplayName => Comment.Length > 0 ? Comment : Realm;

    /// <summary>콤보박스용 라벨 — 기본 영역이면 "(기본)" 표시.</summary>
    public string DisplayLabel => IsDefault ? Res.T("PveAuthDomain_02", DisplayName) : DisplayName;
}
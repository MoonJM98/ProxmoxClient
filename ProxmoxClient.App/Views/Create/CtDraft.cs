namespace ProxmoxClient.App.Views.Create;

/// <summary>CT 마법사의 추가 마운트 포인트(mpN) 하나.</summary>
internal sealed class MountDraft
{
    public string Id { get; set; } = "mp0";
    public string Storage { get; set; } = string.Empty;
    public string SizeGib { get; set; } = "8";
    public string Path { get; set; } = string.Empty;
    public bool Backup { get; set; } = true;
}

/// <summary>
///     CT 만들기 마법사 입력(웹 UI lxc/CreateWizard.js) — <see cref="BuildParams" /> 가 POST nodes/{node}/lxc 로 보낼 값을 만든다.
///     SSH 키는 VM 과 달리 URL 인코딩하지 않은 원문 그대로 보낸다.
/// </summary>
internal sealed class CtDraft
{
    public const int MinPasswordLength = 5;

    // 일반
    public string Node { get; set; } = string.Empty;
    public string VmId { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public bool Unprivileged { get; set; } = true;
    public bool Nesting { get; set; } = true;
    public string Pool { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
    public string SshKeys { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;

    // 템플릿·디스크
    public string Template { get; set; } = string.Empty;
    public string RootStorage { get; set; } = string.Empty;
    public string RootSizeGib { get; set; } = "8";
    public List<MountDraft> Mounts { get; } = [];

    // CPU·메모리
    public string Cores { get; set; } = "1";
    public string CpuLimit { get; set; } = string.Empty;
    public string CpuUnits { get; set; } = string.Empty;
    public string Memory { get; set; } = "512";
    public string Swap { get; set; } = "512";

    // 네트워크 — 이름 eth0, 방화벽 켬, IPv4·IPv6 는 고정(빈 주소)이 기본
    public string NetName { get; set; } = "eth0";
    public string Mac { get; set; } = string.Empty;
    public string Bridge { get; set; } = string.Empty;
    public string Vlan { get; set; } = string.Empty;
    public bool Firewall { get; set; } = true;
    public string V4Mode { get; set; } = "static";
    public string Ip { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public string V6Mode { get; set; } = "static";
    public string Ip6 { get; set; } = string.Empty;
    public string Gateway6 { get; set; } = string.Empty;

    // DNS·확인
    public string SearchDomain { get; set; } = string.Empty;
    public string Nameserver { get; set; } = string.Empty;
    public bool Start { get; set; }

    public IReadOnlyDictionary<string, string> BuildParams()
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vmid"] = VmId.Trim(), ["ostemplate"] = Template,
            ["unprivileged"] = Unprivileged ? "1" : "0",
            ["rootfs"] = $"{RootStorage}:{RootSizeGib.Trim()}",
            ["cores"] = Cores.Trim(), ["memory"] = Memory.Trim(), ["swap"] = Swap.Trim(),
            ["net0"] = NetValue()
        };
        void Put(string key, string value)
        {
            if (value.Trim().Length > 0) p[key] = value.Trim();
        }

        Put("hostname", Hostname);
        if (Unprivileged && Nesting) p["features"] = "nesting=1"; // 특권 CT 는 nesting 칸이 꺼진다
        Put("pool", Pool);
        if (Password.Length > 0) p["password"] = Password; // 입력 그대로(앞뒤 공백도 비밀번호의 일부)
        if (SshKeys.Trim().Length > 0) p["ssh-public-keys"] = SshKeys.Replace("\r\n", "\n");
        Put("tags", string.Join(';', Tags.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)));
        foreach (var m in Mounts)
            p[m.Id] = $"{m.Storage}:{m.SizeGib.Trim()},mp={m.Path.Trim()}" + (m.Backup ? ",backup=1" : "");
        Put("cpulimit", CpuLimit == "0" ? "" : CpuLimit);
        Put("cpuunits", CpuUnits);
        Put("searchdomain", SearchDomain);
        Put("nameserver", string.Join(' ', Nameserver.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries)));
        if (Start) p["start"] = "1";
        return p;
    }

    /// <summary>예: name=eth0,bridge=vmbr0,firewall=1[,ip=dhcp][,ip6=auto] — 빈 값은 뺀다.</summary>
    internal string NetValue()
    {
        var v4Static = V4Mode != "dhcp";
        var v6Static = V6Mode is not ("dhcp" or "auto");
        var parts = new List<(string, string)>
        {
            ("name", NetName.Trim()), ("hwaddr", Mac.Trim()), ("bridge", Bridge), ("tag", Vlan.Trim()),
            ("firewall", Firewall ? "1" : ""),
            ("ip", v4Static ? Ip.Trim() : "dhcp"), ("gw", v4Static ? Gateway.Trim() : ""),
            ("ip6", v6Static ? Ip6.Trim() : V6Mode), ("gw6", v6Static ? Gateway6.Trim() : "")
        };
        return string.Join(',', parts.Where(x => x.Item2.Length > 0).Select(x => $"{x.Item1}={x.Item2}"));
    }

    /// <summary>비밀번호 — SSH 키가 없으면 5자 이상 필수, 확인 칸과 같아야 한다.</summary>
    public string? PasswordProblem()
    {
        if (Password != ConfirmPassword) return "Ct_PasswordMismatch";
        if (Password.Length == 0) return SshKeys.Trim().Length > 0 ? null : "Ct_PasswordOrKey";
        if (Password.Trim().Length == 0) return "Ct_PasswordBlank";
        return Password.Length < MinPasswordLength ? "Ct_PasswordShort" : null;
    }
}

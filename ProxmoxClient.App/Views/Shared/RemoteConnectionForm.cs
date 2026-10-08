using System.Globalization;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Files;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>게스트 주소·열린 파일 서비스 포트를 미리 알아본 결과(설정 창 안내·기본 주소용).</summary>
internal sealed record RemoteDetection(IReadOnlyList<string> Addresses, string? Host, IReadOnlySet<int> OpenPorts)
{
    public static readonly RemoteDetection None = new([], null, new HashSet<int>());

    /// <summary>알아본 포트와 이름(설정 창 안내에 이 순서로).</summary>
    public static readonly (int Port, string Name)[] Services =
    [
        (22, "SSH/SFTP"), (RemoteFileSettings.SmbPort, "SMB"), (RemoteFileSettings.FtpPort, "FTP"),
        (RemoteFileSettings.FtpsImplicitPort, "FTPS")
    ];

    /// <summary>안내 문구 — 게스트 IP 와 열린 포트.</summary>
    public string Summary()
    {
        if (Addresses.Count == 0) return Loc.T("Remote_NoAddress");

        var open = Services.Where(s => OpenPorts.Contains(s.Port)).Select(s => $"{s.Name}({s.Port})").ToList();
        var ports = open.Count > 0 ? Loc.T("Remote_OpenPorts", string.Join(" · ", open)) : Loc.T("Remote_NoOpenPorts");
        return Loc.T("Remote_Detected", string.Join(", ", Addresses)) + "\n" + ports;
    }
}

/// <summary>SMB·FTP 접속 설정 창(공용 입력 창으로) — 주소·공유/포트·암호화·사용자·비밀번호·기억.</summary>
internal static class RemoteConnectionForm
{
    /// <summary>설정 창을 띄운다 — 확인하면 (설정, 입력한 비밀번호), 취소하면 null.</summary>
    public static (RemoteFileSettings Settings, string Password)? Show(Window owner, GuestFileTransport transport,
        string guestTitle, RemoteFileSettings current, RemoteDetection detection)
    {
        var smb = transport == GuestFileTransport.Smb;
        var host = current.Host.Length > 0 ? current.Host : detection.Host ?? string.Empty;
        var fields = new List<FormField>
        {
            // 게스트에서 알아본 IP·열린 포트 — 툴팁이 아니라 위에 바로 보이게(구역 안내 글)
            new() { Key = "guest", LabelKey = "Remote_GuestInfo", Kind = FormFieldKind.Section,
                    Hint = detection.Summary() },
            new() { Key = "host", LabelKey = "Remote_Host", Required = true, Initial = host }
        };
        if (smb)
            fields.Add(new FormField
                { Key = "share", LabelKey = "Remote_Share", Required = true, Initial = current.Share,
                  Hint = Loc.T("Remote_ShareHint") });
        else
        {
            fields.Add(new FormField
            {
                Key = "port", LabelKey = "Remote_Port", Hint = Loc.T("Remote_PortHint"),
                Initial = current.Port > 0 ? current.Port.ToString(CultureInfo.InvariantCulture) : string.Empty
            });
            fields.Add(new FormField
            {
                Key = "security", LabelKey = "Remote_Security", Kind = FormFieldKind.Choice,
                Initial = current.Security.ToString(),
                Choices =
                [
                    (nameof(FtpSecurity.Explicit), Loc.T("Remote_SecExplicit")),
                    (nameof(FtpSecurity.Implicit), Loc.T("Remote_SecImplicit")),
                    (nameof(FtpSecurity.None), Loc.T("Remote_SecNone"))
                ]
            });
        }

        fields.AddRange(CredentialFields(smb, current));
        var title = Loc.T(smb ? "Remote_SmbTitle" : "Remote_FtpTitle", guestTitle);
        var dialog = new FormDialog(title, fields, Validate) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } r) return null;

        var settings = new RemoteFileSettings
        {
            Host = r["host"], Share = r.GetValueOrDefault("share", string.Empty),
            Port = int.TryParse(r.GetValueOrDefault("port"), out var port) ? port : 0,
            Security = Enum.TryParse<FtpSecurity>(r.GetValueOrDefault("security"), out var security)
                ? security
                : FtpSecurity.Explicit,
            UserName = r["user"], RememberPassword = r["remember"] == "1"
        }.Normalize();
        return (settings, r["password"]);
    }

    private static IEnumerable<FormField> CredentialFields(bool smb, RemoteFileSettings current) =>
    [
        new() { Key = "user", LabelKey = "Remote_User", Initial = current.UserName,
                Hint = Loc.T(smb ? "Remote_UserHintSmb" : "Remote_UserHintFtp") },
        new() { Key = "password", LabelKey = "Remote_Password", Kind = FormFieldKind.Password,
                Hint = Loc.T("Remote_PasswordHint") },
        new() { Key = "remember", LabelKey = "Remote_Remember", Kind = FormFieldKind.Bool,
                Initial = current.RememberPassword ? "1" : "0" }
    ];

    private static string? Validate(IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(values["host"])) return Loc.T("Remote_BadHost");
        if (values.TryGetValue("share", out var share) && string.IsNullOrWhiteSpace(share))
            return Loc.T("Remote_BadShare");
        if (values.TryGetValue("port", out var port) && port.Trim().Length > 0
                                                     && !(int.TryParse(port, out var p) && p is >= 1 and <= 65535))
            return Loc.T("Remote_BadPort");
        return values["password"].Length > 1280 ? Loc.T("Sftp_PasswordLong") : null;
    }

    /// <summary>접속할 때 비밀번호를 묻는다 — (비밀번호, 기억) 또는 취소면 null.</summary>
    public static (string Password, bool Remember)? AskPassword(Window owner, RemoteFileSettings settings,
        bool failed)
    {
        var title = Loc.T(failed ? "Remote_AuthFailed" : "Remote_PasswordFor", settings.UserName, settings.Host);
        var dialog = new FormDialog(title,
        [
            new() { Key = "password", LabelKey = "Remote_Password", Kind = FormFieldKind.Password },
            new() { Key = "remember", LabelKey = "Remote_Remember", Kind = FormFieldKind.Bool,
                    Initial = settings.RememberPassword ? "1" : "0" }
        ], v => v["password"].Length > 1280 ? Loc.T("Sftp_PasswordLong") : null) { Owner = owner };
        return dialog.ShowDialog() == true && dialog.Result is { } r ? (r["password"], r["remember"] == "1") : null;
    }
}

using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Node;

/// <summary>
///     노드 인증서 — 직접 준비한 인증서 올리기/지우기와 ACME(Let's Encrypt 등) 도메인 설정·발급·갱신.
///     인증서가 바뀌면 서버 지문도 바뀌므로 다음 연결 때 지문 확인을 다시 묻는다.
/// </summary>
internal static class CertificateActions
{
    /// <summary>웹 UI 와 같이 노드당 ACME 도메인은 여러 개 둘 수 있지만, 입력 창에는 흔히 쓰는 수만 둔다.</summary>
    private const int AcmeDomainSlots = 3;

    public static IReadOnlyList<TableAction> Actions(ProxmoxApiClient api, string node)
    {
        var basePath = $"nodes/{Seg(node)}";
        return
        [
            new TableAction
            {
                LabelKey = "NodeCert_Upload", IconKey = "IconDownload",
                Run = (_, owner) => SubmitAsync(owner, "NodeCert_UploadTitle",
                [
                    new FormField
                    {
                        Key = "certificates", LabelKey = "NodeCert_Chain", Kind = FormFieldKind.Multiline,
                        Required = true
                    },
                    new FormField { Key = "key", LabelKey = "NodeCert_Key", Kind = FormFieldKind.Multiline }
                ], values =>
                {
                    var form = NonEmpty(values);
                    form["force"] = "1";
                    form["restart"] = "1";
                    return api.PostActionAsync($"{basePath}/certificates/custom", form);
                }, "NodeCert_Uploaded")
            },
            new TableAction
            {
                LabelKey = "NodeCert_DeleteCustom", IconKey = "IconTrash",
                Confirm = _ => Loc.T("NodeCert_DeleteCustomConfirm"),
                Run = async (_, _) =>
                {
                    await api.DeleteActionAsync($"{basePath}/certificates/custom?restart=1");
                    return Loc.T("NodeCert_CustomDeleted");
                }
            },
            new TableAction
            {
                LabelKey = "NodeCert_AcmeSettings", IconKey = "IconSettings",
                Run = (_, owner) => EditAcmeAsync(api, basePath, owner)
            },
            new TableAction
            {
                LabelKey = "NodeCert_AcmeOrder", IconKey = "IconShield",
                Confirm = _ => Loc.T("NodeCert_AcmeOrderConfirm"),
                Run = async (_, _) => await RunTaskAsync(api,
                    api.PostActionAsync($"{basePath}/certificates/acme/certificate",
                        new Dictionary<string, string> { ["force"] = "1" }), "NodeCert_AcmeOrdered")
            },
            new TableAction
            {
                LabelKey = "NodeCert_AcmeRenew", IconKey = "IconRefresh",
                Run = async (_, _) => await RunTaskAsync(api,
                    api.PutActionAsync($"{basePath}/certificates/acme/certificate",
                        new Dictionary<string, string> { ["force"] = "1" }), "NodeCert_AcmeRenewed")
            }
        ];
    }

    /// <summary>ACME 계정·도메인·검증 방식(플러그인) 설정 — 노드 설정의 acme, acmedomain0.. 항목.</summary>
    private static async Task<string?> EditAcmeAsync(ProxmoxApiClient api, string basePath, Window? owner)
    {
        var config = await api.GetObjectAsync($"{basePath}/config");
        var accounts = (await api.GetTableAsync("cluster/acme/account"))
            .Select(a => (Value(a, "name"), Value(a, "name")))
            .ToList();
        var plugins = (await api.GetTableAsync("cluster/acme/plugins"))
            .Where(p => Value(p, "type") == "dns")
            .Select(p => (Value(p, "plugin"), Value(p, "plugin")))
            .Prepend((string.Empty, "NodeCert_Standalone"))
            .ToList();

        var current = Enumerable.Range(0, AcmeDomainSlots)
            .Select(i => AcmeDomain.Parse(Value(config, $"acmedomain{i}")))
            .ToList();
        var account = AcmeDomain.ParseProperty(Value(config, "acme"), "account");

        var fields = new List<FormField>
        {
            new()
            {
                Key = "account", LabelKey = "NodeCert_AcmeAccount", Kind = FormFieldKind.Choice, Choices = accounts,
                Initial = account.Length > 0 ? account : "default"
            },
            new()
            {
                Key = "plugin", LabelKey = "NodeCert_AcmePlugin", Kind = FormFieldKind.Choice, Choices = plugins,
                Initial = current.FirstOrDefault(d => d.Plugin.Length > 0)?.Plugin ?? string.Empty
            }
        };
        for (var i = 0; i < AcmeDomainSlots; i++)
            fields.Add(new FormField
            {
                Key = $"domain{i}", LabelKey = "NodeCert_AcmeDomain", Initial = current[i].Domain
            });

        return await SubmitAsync(owner, "NodeCert_AcmeSettings", fields, values =>
        {
            var form = new Dictionary<string, string>(StringComparer.Ordinal);
            var cleared = new List<string>();
            if (values["account"].Length > 0) form["acme"] = $"account={values["account"]}";

            for (var i = 0; i < AcmeDomainSlots; i++)
            {
                var domain = values[$"domain{i}"];
                if (domain.Length == 0)
                {
                    if (current[i].Domain.Length > 0) cleared.Add($"acmedomain{i}");
                    continue;
                }

                form[$"acmedomain{i}"] = new AcmeDomain(domain, values["plugin"]).ToString();
            }

            if (cleared.Count > 0) form["delete"] = string.Join(",", cleared);
            return api.PutActionAsync($"{basePath}/config", form);
        }, "NodeCert_AcmeSaved");
    }
}

/// <summary>노드 설정의 ACME 도메인 한 줄 — "domain=example.com,plugin=cf" 또는 도메인만.</summary>
internal sealed record AcmeDomain(string Domain, string Plugin)
{
    public static AcmeDomain Parse(string raw)
    {
        if (raw.Length == 0) return new AcmeDomain(string.Empty, string.Empty);

        var domain = ParseProperty(raw, "domain");
        if (domain.Length == 0) domain = raw.Split(',')[0].Trim(); // 키 없이 도메인만 적은 형식
        return new AcmeDomain(domain, ParseProperty(raw, "plugin"));
    }

    /// <summary>"a=1,b=2" 형식에서 한 키의 값. 없으면 빈 문자열.</summary>
    public static string ParseProperty(string raw, string key)
    {
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq] == key) return part[(eq + 1)..];
        }

        return string.Empty;
    }

    public override string ToString()
    {
        return Plugin.Length > 0 ? $"domain={Domain},plugin={Plugin}" : $"domain={Domain}";
    }
}

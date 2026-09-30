using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Versioning;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Storage;

/// <summary>
///     게스트 가져오기(웹 UI 의 Import, 8.1+) — ESXi 저장소의 VM(.vmx)이나 OVA 를 골라 새 VM 으로 만든다. 서버가 읽어 준
///     설정으로 VM 을 만들고, 디스크는 고른 저장소로 복사하며, 네트워크 카드는 고른 브리지에 원래 MAC 으로 붙인다.
/// </summary>
internal static class ImportGuestAction
{
    private static readonly string[] GuestExtensions = [".vmx", ".ova", ".ovf"];

    public static TableAction Create(ProxmoxApiClient api, string node, string storage)
    {
        return new TableAction
        {
            LabelKey = "Import_Action", IconKey = "IconDownload", NeedsSelection = true,
            Requires = new ApiFeature(() => api.Supports(new PveApiVersion(8, 1))),
            Run = (row, owner) =>
            {
                var volume = row!.TryGetValue("volid", out var v) ? v : string.Empty;
                // 디스크 이미지(.vmdk 등)는 게스트가 아니라 가져올 수 없다 — 서버 오류 대신 먼저 알린다
                return GuestExtensions.Any(e => volume.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                    ? RunAsync(api, node, storage, volume, owner)
                    : Task.FromResult<string?>(Loc.T("Import_NotGuest"));
            }
        };
    }

    private static async Task<string?> RunAsync(ProxmoxApiClient api, string node, string storage, string volume,
        Window? owner)
    {
        var meta = await api.GetImportMetadataAsync(node, storage, volume);
        var nextId = await api.GetNextVmIdAsync() ?? 100;
        var storages = (await api.Storage.NodeStoragesAsync(node, "images"))
            .Select(s => (Value(s, "storage"), Value(s, "storage"))).Where(s => s.Item1.Length > 0).ToList();
        var bridges = (await api.GetNodeBridgesAsync(node)).Select(b => (b, b)).ToList();
        if (storages.Count == 0) return Loc.T("Import_NoStorage", node);
        if (bridges.Count == 0) return Loc.T("Import_NoBridge", node);

        var fields = new List<FormField>
        {
            new() { Key = "vmid", LabelKey = "Table_Id", Required = true, Trim = true,
                Initial = nextId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            new() { Key = "name", LabelKey = "Table_Name", Trim = true,
                Initial = meta.CreateArgs.TryGetValue("name", out var name) ? name : string.Empty },
            new() { Key = "storage", LabelKey = "Import_TargetStorage", Kind = FormFieldKind.Choice,
                Choices = storages, Initial = storages[0].Item1, Required = true,
                Hint = Loc.T("Import_Disks", meta.Disks.Count == 0
                    ? "-"
                    : string.Join("\n", meta.Disks.Select(d => $"{d.Key}: {d.Value}"))) },
            new() { Key = "bridge", LabelKey = "Import_Bridge", Kind = FormFieldKind.Choice, Choices = bridges,
                Initial = bridges[0].Item1, Required = true,
                Hint = Loc.T("Import_Nics", meta.Nets.Count) }
        };
        if (meta.Warnings.Count > 0)
            fields.Insert(0, new FormField
            {
                Key = "_warnings", Kind = FormFieldKind.Section,
                LabelKey = Loc.T("Import_Warnings", string.Join(", ", meta.Warnings))
            });

        return await SubmitTaskAsync(api, owner, Loc.T("Import_Title", volume), fields,
            values => api.Guests.CreateAsync(node, ResourceKind.Qemu, ImportPlan.BuildCreateForm(meta,
                int.Parse(values["vmid"], System.Globalization.CultureInfo.InvariantCulture), values["name"],
                values["storage"], values["bridge"])),
            "Import_Done", values => int.TryParse(values["vmid"], out var id) && id >= 100
                ? null
                : Loc.T("Import_BadVmid"));
    }
}

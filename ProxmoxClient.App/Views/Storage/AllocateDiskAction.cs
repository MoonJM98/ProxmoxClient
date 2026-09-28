using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Storage;

/// <summary>빈 디스크 이미지 만들기 — 저장소에만 만들고 VM 에는 붙이지 않는다(하드웨어에서 기존 디스크로 붙인다).</summary>
internal static class AllocateDiskAction
{
    public static TableAction Create(ProxmoxApiClient api, string node, string storage)
    {
        return new TableAction
        {
            LabelKey = "AllocDisk_Action", IconKey = "IconPlus",
            Run = (_, owner) => SubmitAsync(owner, "AllocDisk_Action",
            [
                new FormField { Key = "vmid", LabelKey = "Table_Guest", Required = true, Trim = true,
                    Hint = Loc.T("AllocDisk_VmidHint") },
                new FormField { Key = "filename", LabelKey = "AllocDisk_FileName", Required = true, Trim = true,
                    Hint = Loc.T("AllocDisk_FileNameHint") },
                new FormField { Key = "size", LabelKey = "Table_Size", Required = true, Trim = true, Initial = "32G",
                    Hint = Loc.T("AllocDisk_SizeHint") },
                new FormField { Key = "format", LabelKey = "StorageContent_Format", Kind = FormFieldKind.Choice,
                    Choices = [("", "StorageField_Default"), ("raw", "raw"), ("qcow2", "qcow2"), ("vmdk", "vmdk")] }
            ], async values =>
            {
                await api.Storage.AllocateDiskAsync(node, storage, int.Parse(values["vmid"],
                    System.Globalization.CultureInfo.InvariantCulture), values["filename"], values["size"],
                    values["format"]);
                return string.Empty;
            }, "AllocDisk_Done", validate: values => int.TryParse(values["vmid"], out var id) && id >= 100
                ? null
                : Loc.T("Import_BadVmid"))
        };
    }
}

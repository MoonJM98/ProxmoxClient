namespace ProxmoxClient.Core.Models;

/// <summary>
///     가져올 게스트의 정보(GET .../import-metadata) — 새 VM 설정(create-args), 디스크(버스 → 원본 볼륨), 네트워크
///     카드(net0 → MAC·모델), 알려진 문제(warnings).
/// </summary>
public sealed record ImportMetadata(
    IReadOnlyDictionary<string, string> CreateArgs,
    IReadOnlyDictionary<string, string> Disks,
    IReadOnlyDictionary<string, ImportNic> Nets,
    IReadOnlyList<string> Warnings);

/// <summary>가져올 네트워크 카드 — 원래 MAC 과 모델(없으면 null).</summary>
public sealed record ImportNic(string? MacAddress, string? Model);

/// <summary>
///     가져오기 정보로 새 VM 만들기 요청을 짠다(웹 UI 가져오기 마법사와 같은 규칙) — 디스크는 대상 저장소에
///     "저장소:0,import-from=원본" 으로 새로 만들며 복사하고, 네트워크 카드는 고른 브리지에 원래 MAC 으로 붙인다.
///     UEFI(ovmf) 게스트는 EFI 변수를 옮길 수 없어 빈 EFI 디스크를 새로 만든다.
/// </summary>
public static class ImportPlan
{
    /// <summary>모델을 알 수 없는 네트워크 카드 — ESXi 기본 장치와 같은 vmxnet3.</summary>
    private const string DefaultNicModel = "vmxnet3";

    public static Dictionary<string, string> BuildCreateForm(ImportMetadata meta, int vmid, string name,
        string targetStorage, string bridge)
    {
        var form = new Dictionary<string, string>(meta.CreateArgs, StringComparer.Ordinal)
        {
            ["vmid"] = vmid.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(name)) form["name"] = name.Trim();

        foreach (var (key, volume) in meta.Disks)
            form[key] = $"{targetStorage}:0,import-from={volume}";

        if (form.TryGetValue("bios", out var bios) && bios == "ovmf" && !form.ContainsKey("efidisk0"))
            form["efidisk0"] = $"{targetStorage}:1,efitype=4m";

        foreach (var (key, nic) in meta.Nets)
        {
            var model = string.IsNullOrWhiteSpace(nic.Model) ? DefaultNicModel : nic.Model;
            var device = string.IsNullOrWhiteSpace(nic.MacAddress) ? model : $"{model}={nic.MacAddress}";
            form[key] = $"{device},bridge={bridge}";
        }

        return form;
    }
}

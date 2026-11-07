using Avalonia.Controls;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Views.Shared;
using static ProxmoxClient.Presentation.Shared.FormHelpers;

namespace ProxmoxClient.UI.Views.Storage;

/// <summary>
///     저장소 창 — 탭은 공용 <see cref="StorageTabs" />. 여기에는 창과, 이 앱의 올리기(파일 선택기 스트림 · 진행 시트)만 둔다.
/// </summary>
public static class StorageNavigator
{
    /// <summary>
    ///     저장소 창을 만든다. <paramref name="pluginType" /> 은 저장소 유형(pbs 면 백업 속 파일 복원) — 모르면 null
    ///     (버튼을 두고 눌렀을 때 판단).
    /// </summary>
    public static NavWindow Create(ProxmoxApiClient api, string node, string storage, string content,
        PermissionsInfo permissions, string? pluginType = null)
    {
        var title = Loc.T("StorageWindow_Title", storage, node);
        return new NavWindow(title, title, "IconDatabase",
            Tabs(api, node, storage, content, permissions, pluginType), null);
    }

    /// <summary>탭 목록 — 저장소 창과 검증용 미리보기가 같이 쓴다.</summary>
    internal static List<NavTab> Tabs(ProxmoxApiClient api, string node, string storage, string content,
        PermissionsInfo permissions, string? pluginType)
    {
        return StorageTabs.Tabs(api, node, storage, content, permissions, pluginType).Select(ViewSpecs.Tab).ToList();
    }

    /// <summary>파일을 고르고 진행 시트를 띄워 올린 뒤, 서버 작업이 끝나면 결과를 돌려준다(취소면 취소 문구).</summary>
    public static async Task<string?> UploadAsync(ProxmoxApiClient api, string node, string storage, string content,
        object? owner)
    {
        var picked = await StorageFiles.PickUploadAsync(owner,
            Loc.T(content == "iso" ? "StorageUpload_IsoFilter" : "StorageUpload_TemplateFilter"),
            Loc.T("StorageUpload_Button"));
        if (picked is null) return null;

        // 서버는 확장자가 맞지 않으면 다 받은 뒤에야 거절한다 — 보내기 전에 막는다
        if (!ProxmoxApiClient.IsUploadExtensionOk(content, picked.Name))
            return Loc.T(content == "iso" ? "StorageUpload_BadIsoExt" : "StorageUpload_BadTemplateExt", picked.Name);

        // 선택 사항: 받은 뒤 서버가 검사할 체크섬
        var options = await FormDialog.ShowAsync(Loc.T("StorageUpload_Title", picked.Name),
        [
            new FormField
            {
                Key = "algorithm", LabelKey = "StorageUpload_ChecksumAlgorithm", Kind = FormFieldKind.Choice,
                Choices = ChecksumAlgorithms, Initial = string.Empty, Advanced = true
            },
            new FormField
            {
                Key = "checksum", LabelKey = "StorageUpload_Checksum", Advanced = true,
                Hint = Loc.T("StorageUpload_ChecksumHint")
            }
        ], ValidateChecksum);
        if (options is null) return null;

        var algorithm = options.GetValueOrDefault("algorithm") ?? string.Empty;
        var checksum = options.GetValueOrDefault("checksum")?.Trim() ?? string.Empty;
        (string, string)? expected = algorithm.Length > 0 && checksum.Length > 0 ? (checksum, algorithm) : null;

        // 파일 선택기 스트림을 그대로 흘려 보낸다(예전에는 임시 폴더에 통째로 복사한 뒤 올렸다 — 수 GB ISO 면
        // 기기 저장 공간이 모자라고 오래 걸렸다). 크기를 모르는 공급자일 때만 복사본을 만든다
        await using var source = await UploadSource.OpenAsync(picked);
        using var cancel = new CancellationTokenSource();
        var sheet = new UploadProgressSheet(picked.Name, cancel);
        var upid = string.Empty;
        try
        {
            _ = AppServices.Dialogs.ShowSheetAsync(sheet);
            upid = await api.UploadToStorageAsync(node, storage, content, source.TakeStream(), source.Length,
                picked.Name, expected, sheet.Progress, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return Loc.T("StorageUpload_Cancelled");
        }
        finally
        {
            sheet.Close();
        }

        // 올리기는 끝났다 — 서버가 옮기는 작업은 진행 시트 없이 기다린다(WPF 와 같음)
        return await RunTaskAsync(api, Task.FromResult(upid), "StorageUpload_Done");
    }

    private static readonly IReadOnlyList<(string, string)> ChecksumAlgorithms =
    [
        ("", "StorageUpload_NoChecksum"), ("sha256", "SHA-256"), ("sha512", "SHA-512"), ("sha384", "SHA-384"),
        ("sha224", "SHA-224"), ("sha1", "SHA-1"), ("md5", "MD5")
    ];

    private static string? ValidateChecksum(IReadOnlyDictionary<string, string> values)
    {
        var algorithm = values.GetValueOrDefault("algorithm") ?? string.Empty;
        var checksum = values.GetValueOrDefault("checksum")?.Trim() ?? string.Empty;
        if (algorithm.Length == 0 && checksum.Length == 0) return null;
        if (algorithm.Length == 0 || checksum.Length == 0) return Loc.T("StorageUpload_ChecksumBoth");
        return checksum.All(Uri.IsHexDigit) ? null : Loc.T("StorageUpload_ChecksumHint");
    }
}

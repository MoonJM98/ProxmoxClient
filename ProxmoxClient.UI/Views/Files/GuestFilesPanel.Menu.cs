using System.Text.Json;
using ProxmoxClient.Core.Vnc;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Views.Files;

/// <summary>숨김 파일 보기 — 숨김·시스템 항목은 켜면 흐리게 보이고, 끄면 목록에서 뺀다(콘솔 설정에 기억).</summary>
public partial class GuestFilesPanel
{
    private bool _showHidden = true;
    private bool _hiddenTouched; // 설정을 읽기 전에 사용자가 바꿨으면 읽은 값으로 덮지 않는다
    private bool _applyingHidden;

    /// <summary>숨김 파일 보기 설정을 읽어 둔다(없거나 못 읽으면 보임).</summary>
    private async Task LoadViewSettingsAsync()
    {
        try
        {
            var settings = await new ConsoleSettingsStore().LoadAsync();
            if (!_hiddenTouched) SetShowHidden(settings.ShowHiddenGuestFiles, save: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[게스트 파일] 보기 설정 읽기 실패: {ex.Message}");
        }
    }

    private void OnToggleHidden()
    {
        if (_applyingHidden) return;

        _hiddenTouched = true;
        SetShowHidden(_hiddenButton.IsChecked == true, save: true);
    }

    private void SetShowHidden(bool show, bool save)
    {
        _showHidden = show;
        _applyingHidden = true;
        try
        {
            _hiddenButton.IsChecked = show;
        }
        finally
        {
            _applyingHidden = false;
        }

        if (_lastEntries is { } entries) ShowEntries(_current, entries);
        if (save) _ = SaveHiddenAsync(show);
    }

    private static async Task SaveHiddenAsync(bool show)
    {
        try
        {
            // 다른 화면이 바꾼 콘솔 설정을 덮지 않게 저장 직전에 다시 읽어 이 값만 바꾼다
            await new ConsoleSettingsStore().UpdateAsync(settings => settings with { ShowHiddenGuestFiles = show });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[게스트 파일] 숨김 파일 설정 저장 실패: {ex.Message}");
        }
    }
}

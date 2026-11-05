using Avalonia.Controls;
using Avalonia.Input.Platform;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views;

public static class ClipboardHelper
{
    /// <summary>셸이 붙은 최상위 — 클립보드는 여기서 얻는다.</summary>
    internal static TopLevel? TopLevel { get; set; }

    public static async Task SetTextAsync(string text)
    {
        if (TopLevel?.Clipboard is not { } clipboard) return;

        await clipboard.SetTextAsync(text);
        AppServices.Dialogs.Toast(L.T("Common_Copied"));
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Controls;

namespace ProxmoxClient.UI.Views.Storage;

/// <summary>
///     업로드 진행 시트 — WPF UploadProgressWindow 와 같은 배치(파일 이름 · 진행 막대 · 퍼센트 · 취소).
///     화면 위에 뜨는 시트이므로 창을 닫는 대신 취소 단추로 업로드를 멈춘다(시트는 업로드가 끝나면 스스로 닫힌다).
/// </summary>
internal sealed class UploadProgressSheet : UserControl
{
    private readonly CancellationTokenSource _cancel;
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 16 };
    private readonly TextBlock _percent = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "subtle" } };

    public UploadProgressSheet(string fileName, CancellationTokenSource cancel)
    {
        _cancel = cancel;
        Width = 440;
        Progress = new Progress<double>(OnProgress);

        var cancelButton = new Button
        {
            Content = Loc.T("AppSettingsWindow_13"), Classes = { "DialogButton" }, MinHeight = 40
        };
        IconAssist.SetIcon(cancelButton, StorageFiles.Icon("IconX"));
        cancelButton.Click += (_, _) => _cancel.Cancel();

        var footer = new DockPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                cancelButton,
                _percent
            }
        };
        DockPanel.SetDock(cancelButton, Dock.Right);

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Children =
            {
                new TextBlock { Text = Loc.T("StorageUpload_Title"), Classes = { "headline" }, Margin = new Thickness(0, 0, 0, 8) },
                new TextBlock
                {
                    Text = fileName, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8)
                },
                _bar,
                footer
            }
        };
        _percent.Text = Loc.T("StorageUpload_Percent", 0.0);
    }

    /// <summary>보낸 비율(0~1)을 받는다 — 업로드 쪽에 넘긴다.</summary>
    public IProgress<double> Progress { get; }

    /// <summary>
    ///     시트를 닫는다 — 아직 이 시트가 떠 있을 때만 닫아, 다른 대화상자를 건드리지 않는다.
    /// </summary>
    public void Close()
    {
        if (ReferenceEquals(AppServices.Dialogs.Sheet, this)) AppServices.Dialogs.CloseSheet();
    }

    private void OnProgress(double fraction)
    {
        _bar.Value = Math.Clamp(fraction * 100, 0, 100);
        _percent.Text = Loc.T("StorageUpload_Percent", _bar.Value);
    }
}

using System.Windows;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views.Storage;

/// <summary>업로드 진행 창 — 진행률을 보여 주고, 취소를 누르면 업로드를 멈춘다.</summary>
public partial class UploadProgressWindow : Window
{
    private readonly CancellationTokenSource _cancel = new();

    public UploadProgressWindow(string fileName)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Title = Loc.T("StorageUpload_Title");
        FileText.Text = fileName;
        Progress = new Progress<double>(fraction =>
        {
            Bar.Value = Math.Clamp(fraction * 100, 0, 100);
            PercentText.Text = Loc.T("StorageUpload_Percent", Bar.Value);
        });
        // 창을 닫으면 업로드도 멈춘다(토큰은 업로드가 끝날 때까지 쓰이므로 여기서 해제하지 않는다)
        Closing += (_, _) => _cancel.Cancel();
    }

    public IProgress<double> Progress { get; }

    public CancellationToken Token => _cancel.Token;

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cancel.Cancel();
    }
}

using Avalonia.Controls;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Views;

public partial class ClusterPage : TabbedPage
{
    public ClusterPage()
    {
        InitializeComponent();
    }

    /// <summary>당겨서 새로 고침.</summary>
    private async void OnRefreshRequested(object? sender, RefreshRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (DataContext is ClusterViewModel vm) await vm.RefreshAsync(true);
        }
        finally
        {
            deferral.Complete();
        }
    }
}

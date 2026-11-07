using Avalonia.Controls;

namespace ProxmoxClient.UI.Views;

public partial class ProfilesPage : ContentPage
{
    public ProfilesPage()
    {
        InitializeComponent();
#if DEBUG
        RenderInfoLine.IsVisible = true;
#endif
    }
}

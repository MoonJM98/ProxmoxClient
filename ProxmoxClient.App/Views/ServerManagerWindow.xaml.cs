using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.App.Views;

public partial class ServerManagerWindow : Window
{
    private readonly ProfileStore _store = new();

    public ServerManagerWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Loaded += async (_, _) => await LoadProfilesAsync();
    }

    private ConnectionProfile? Selected => ProfileList.SelectedItem as ConnectionProfile;

    private async Task LoadProfilesAsync()
    {
        var profiles = await _store.LoadAsync();
        ProfileList.ItemsSource = profiles;
        if (profiles.Count > 0 && Selected is null) ProfileList.SelectedIndex = 0;
    }

    private async void OnAddProfile(object sender, RoutedEventArgs e)
    {
        var register = new ProfileRegisterWindow { Owner = this };
        if (register.ShowDialog() == true && register.ResultProfile is { } profile)
        {
            await _store.SaveAsync(profile); // 자격 증명은 직렬화에서 제외됨
            await LoadProfilesAsync();
            SelectById(profile.Id);
            SetStatus(Loc.T("ServerManagerWindow_M01", profile.Name));
        }
    }

    private async void OnEditProfile(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile)
        {
            SetStatus(Loc.T("ServerManagerWindow_M02"));
            return;
        }

        var dialog = new ProfileDialog(profile) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ResultProfile is not null)
        {
            await _store.SaveAsync(dialog.ResultProfile);
            await LoadProfilesAsync();
            SelectById(dialog.ResultProfile.Id);
            SetStatus(Loc.T("ServerManagerWindow_M03"));
        }
    }

    private async void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile)
        {
            SetStatus(Loc.T("ServerManagerWindow_M04"));
            return;
        }

        var answer = ThemedMessageBox.Show(this, Loc.T("ServerManagerWindow_M05", profile.Name),
            Loc.T("FirewallWindow_M08"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        await _store.DeleteAsync(profile.Id);
        await LoadProfilesAsync();
        SetStatus(Loc.T("ServerManagerWindow_M06", profile.Name));
    }

    private void SelectById(Guid id)
    {
        if (ProfileList.ItemsSource is IReadOnlyList<ConnectionProfile> items)
            ProfileList.SelectedItem = items.FirstOrDefault(p => p.Id == id) ?? ProfileList.SelectedItem;
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }
}
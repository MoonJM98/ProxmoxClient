using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Views.Login;

/// <summary>"서버 관리"(WPF ServerManagerWindow) — 저장된 서버 목록에서 추가·편집·삭제.</summary>
public sealed partial class ServerManagerViewModel : ViewModelBase
{
    public ObservableCollection<ProfileItem> Profiles { get; } = [];

    [ObservableProperty] public partial ProfileItem? Selected { get; set; }

    [ObservableProperty] public partial string Status { get; set; } = string.Empty;

    /// <summary>목록을 다시 읽는다. 선택은 <paramref name="selectId" /> 또는 이전 선택 서버를 유지한다.</summary>
    public async Task RefreshAsync(Guid? selectId = null)
    {
        var previous = selectId ?? Selected?.Profile.Id;
        var profiles = await AppServices.Profiles.LoadAsync();
        Profiles.Clear();
        foreach (var profile in profiles)
            Profiles.Add(new ProfileItem(profile, AppServices.Profiles.GetSecret(profile) is not null));

        Selected = Profiles.FirstOrDefault(p => p.Profile.Id == previous) ?? Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private Task Add()
    {
        return AppServices.Navigator.PushViewAsync(L.T("ProfileRegisterWindow_01"),
            new ProfileRegisterView(saved =>
            {
                Status = L.T("ServerManagerWindow_M01", saved.Name);
                _ = RefreshAsync(saved.Id);
            }));
    }

    [RelayCommand]
    private Task Edit()
    {
        if (Selected is not { } item)
        {
            Status = L.T("ServerManagerWindow_M02");
            return Task.CompletedTask;
        }

        return AppServices.Navigator.PushAsync(new ProfileEditViewModel(item.Profile)
        {
            Finished = saved =>
            {
                if (saved) Status = L.T("ServerManagerWindow_M03");
                _ = RefreshAsync(item.Profile.Id);
            }
        });
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Selected is not { } item)
        {
            Status = L.T("ServerManagerWindow_M04");
            return;
        }

        var name = item.DisplayName;
        if (!await AppServices.Dialogs.ConfirmAsync(L.T("FirewallWindow_M08"), L.T("ServerManagerWindow_M05", name),
                L.T("FirewallWindow_04"), true))
            return;

        if (!await TryAsync(() => AppServices.Profiles.DeleteAsync(item.Profile))) return;

        await RefreshAsync();
        Status = L.T("ServerManagerWindow_M06", name);
    }

    [RelayCommand]
    private Task Close() => AppServices.Navigator.PopAsync();
}

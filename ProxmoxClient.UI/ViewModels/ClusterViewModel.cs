using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.ViewModels;

public enum GuestFilter
{
    All,
    Running,
    Stopped
}

/// <summary>로그인 후 메인 화면 — 게스트·노드·스토리지·작업 탭. 보이는 동안 5초마다 새로 고친다.</summary>
public sealed partial class ClusterViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private const int MaxTasks = 50;
    private readonly List<PveResource> _allGuests = [];
    private readonly PeriodicRefresher _refresher;

    public ClusterViewModel(ServerSession session)
    {
        Session = session;
        _refresher = new PeriodicRefresher(RefreshInterval, () => RefreshAsync(false));
    }

    public ServerSession Session { get; }

    /// <summary>"관리" 목록 — 데이터센터 화면과 등록된 클러스터 기능(만들기 마법사 등).</summary>
    public IReadOnlyList<MenuEntry> ManagementEntries => _managementEntries ??= BuildManagementEntries();

    private IReadOnlyList<MenuEntry>? _managementEntries;

    private List<MenuEntry> BuildManagementEntries()
    {
        var entries = new List<MenuEntry>();
        if (FeatureRegistry.Datacenter is not null)
            entries.Add(new MenuEntry(L.T("DcWindow_Title"), "IconServer", OpenDatacenterCommand));
        entries.AddRange(FeatureRegistry.ClusterFeatures.OrderBy(f => f.Order)
            .Select(f => new MenuEntry(L.T(f.LabelKey), f.IconKey, RunClusterFeatureCommand, f)));
        return entries;
    }

    public bool HasManagementEntries => ManagementEntries.Count > 0;

    [RelayCommand]
    private Task OpenDatacenter()
    {
        return FeatureRegistry.Datacenter is { } create
            ? AppServices.Navigator.PushViewAsync(L.T("DcWindow_Title"), create(Session))
            : Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RunClusterFeature(ClusterFeature feature)
    {
        try
        {
            await feature.Run(Session);
        }
        catch (Core.Api.WriteCancelledException)
        {
        }
        catch (Exception ex)
        {
            await AppServices.Dialogs.AlertAsync(L.T("Error_Title"), ErrorText.Describe(ex));
        }

        await RefreshAsync(false);
    }

    [RelayCommand]
    private Task OpenStorage(PveStorage storage)
    {
        return FeatureRegistry.Storage is { } create
            ? AppServices.Navigator.PushViewAsync($"{storage.Storage} ({storage.Node})", create(Session, storage))
            : Task.CompletedTask;
    }

    public override string Title => Session.DisplayName;

    public string Subtitle => L.T("Cluster_Subtitle", Session.UserDisplay, Session.Version);

    public ObservableCollection<PveResource> Guests { get; } = [];

    public ObservableCollection<PveNode> Nodes { get; } = [];

    public ObservableCollection<PveStorage> Storages { get; } = [];

    public ObservableCollection<PveTask> Tasks { get; } = [];

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    /// <summary>데스크톱 왼쪽 메뉴에서 고른 영역(0 게스트 · 1 노드 · 2 스토리지 · 3 작업).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuestsSection), nameof(IsNodesSection), nameof(IsStorageSection),
        nameof(IsTasksSection))]
    public partial int SectionIndex { get; set; }

    public bool IsGuestsSection => SectionIndex == 0;

    public bool IsNodesSection => SectionIndex == 1;

    public bool IsStorageSection => SectionIndex == 2;

    public bool IsTasksSection => SectionIndex == 3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterAll), nameof(FilterRunning), nameof(FilterStopped))]
    public partial GuestFilter Filter { get; set; }

    public bool FilterAll
    {
        get => Filter == GuestFilter.All;
        set
        {
            if (value) Filter = GuestFilter.All;
        }
    }

    public bool FilterRunning
    {
        get => Filter == GuestFilter.Running;
        set
        {
            if (value) Filter = GuestFilter.Running;
        }
    }

    public bool FilterStopped
    {
        get => Filter == GuestFilter.Stopped;
        set
        {
            if (value) Filter = GuestFilter.Stopped;
        }
    }

    [ObservableProperty] public partial string GuestSummary { get; private set; } = string.Empty;

    [ObservableProperty] public partial string NodeSummary { get; private set; } = string.Empty;

    [ObservableProperty] public partial bool IsGuestListEmpty { get; private set; }

    /// <summary>마지막 새로 고침 오류(주기 새로 고침은 대화상자 대신 배너로 알린다). 성공하면 비워진다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    [ObservableProperty] public partial bool IsLoaded { get; private set; }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnFilterChanged(GuestFilter value)
    {
        ApplyFilter();
    }

    public override void OnAppearing()
    {
        _refresher.Start();
        _ = RefreshAsync(false);
    }

    public override void OnDisappearing(bool removed)
    {
        _refresher.Stop();
        if (removed) Dispose();
    }

    public void Dispose()
    {
        _refresher.Dispose();
        Session.Dispose();
    }

    [RelayCommand]
    private Task Refresh()
    {
        return RefreshAsync(true);
    }

    /// <summary>클러스터 현황과 최근 작업을 다시 읽는다. <paramref name="userInitiated" /> 가 아니면 오류를 배너로만 알린다.</summary>
    public async Task RefreshAsync(bool userInitiated)
    {
        if (IsRemoved) return;

        IsBusy = true;
        try
        {
            var overviewTask = Session.TakePrefetchedOverview() ?? Session.Api.GetClusterOverviewAsync();
            var tasksTask = Session.Api.GetClusterTasksAsync();
            var overview = await overviewTask;
            IReadOnlyList<PveTask> tasks;
            try
            {
                tasks = await tasksTask;
            }
            catch
            {
                tasks = Tasks.ToList(); // 작업 목록 권한이 없어도 나머지는 보여 준다
            }

            if (IsRemoved) return;

            IsLoaded = true;
            ApplyOverview(overview);
            CollectionSync.Sync(Tasks,
                tasks.OrderByDescending(t => t.StartTimeUtc).Take(MaxTasks).ToList(),
                t => t.Upid, (current, incoming) => current.CopyFrom(incoming));
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ErrorText.Describe(ex);
            if (userInitiated) await AppServices.Dialogs.AlertAsync(L.T("Error_Title"), ErrorMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyOverview(ClusterOverview overview)
    {
        var byId = _allGuests.ToDictionary(g => g.Id, StringComparer.Ordinal);
        _allGuests.Clear();
        foreach (var incoming in overview.Guests.OrderBy(g => g.VmId))
            if (byId.TryGetValue(incoming.Id, out var current))
            {
                current.CopyFrom(incoming);
                _allGuests.Add(current);
            }
            else
            {
                _allGuests.Add(incoming);
            }

        ApplyFilter();

        CollectionSync.Sync(Nodes, overview.Nodes.OrderBy(n => n.Node, StringComparer.OrdinalIgnoreCase).ToList(),
            n => n.Node, (current, incoming) => current.CopyFrom(incoming));
        CollectionSync.Sync(Storages,
            overview.Storages.OrderBy(s => s.Storage, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Node, StringComparer.OrdinalIgnoreCase).ToList(),
            s => s.Id, (current, incoming) => current.CopyFrom(incoming));

        var running = _allGuests.Count(g => g.IsRunning);
        GuestSummary = L.T("Cluster_GuestSummary", running, _allGuests.Count);
        NodeSummary = L.T("Cluster_NodeSummary", Nodes.Count(n => n.IsOnline), Nodes.Count);
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var filtered = _allGuests.Where(g => Filter switch
            {
                GuestFilter.Running => g.IsRunning,
                GuestFilter.Stopped => !g.IsRunning,
                _ => true
            })
            .Where(g => query.Length == 0 ||
                        g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        g.VmId.ToString().Contains(query, StringComparison.Ordinal) ||
                        g.Node.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        g.Tags.Any(t => t.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();
        CollectionSync.Arrange(Guests, filtered);
        IsGuestListEmpty = IsLoaded && filtered.Count == 0;
    }

    [RelayCommand]
    private Task OpenGuest(PveResource guest)
    {
        return AppServices.Navigator.PushAsync(new GuestDetailViewModel(Session, guest));
    }

    [RelayCommand]
    private Task OpenNode(PveNode node)
    {
        return AppServices.Navigator.PushAsync(new NodeDetailViewModel(Session, node,
            _allGuests.Where(g => g.Node == node.Node).ToList()));
    }

    [RelayCommand]
    private Task OpenTask(PveTask task)
    {
        return AppServices.Navigator.PushAsync(new TaskLogViewModel(Session, task));
    }

    [RelayCommand]
    private async Task Logout()
    {
        if (!await AppServices.Dialogs.ConfirmAsync(L.T("Cluster_LogoutTitle"),
                L.T("Cluster_LogoutText", Session.DisplayName), L.T("Cluster_Logout")))
            return;

        await AppServices.Navigator.PopToRootAsync();
    }
}

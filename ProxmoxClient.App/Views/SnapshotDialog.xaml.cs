using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views;

public sealed class SnapshotDialogViewModel : INotifyPropertyChanged
{
    private string _newDescription = string.Empty;
    private bool _newIncludeRam;
    private string _newName = string.Empty;
    private PveSnapshot? _selectedSnapshot;
    private string _statusText = string.Empty;

    public ObservableCollection<PveSnapshot> Snapshots { get; } = [];

    public PveSnapshot? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set => SetField(ref _selectedSnapshot, value);
    }

    public string NewName
    {
        get => _newName;
        set => SetField(ref _newName, value);
    }

    public string NewDescription
    {
        get => _newDescription;
        set => SetField(ref _newDescription, value);
    }

    public bool NewIncludeRam
    {
        get => _newIncludeRam;
        set => SetField(ref _newIncludeRam, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public partial class SnapshotDialog : Window
{
    private readonly ProxmoxApiClient _api;
    private readonly ResourceKind _kind;
    private readonly string _node;
    private readonly SnapshotDialogViewModel _vm = new();
    private readonly int _vmid;
    private bool _busy;

    public SnapshotDialog(ProxmoxApiClient api, string node, ResourceKind kind, int vmid, string guestTitle)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _api = api;
        _node = node;
        _kind = kind;
        _vmid = vmid;
        HeaderText.Text = Loc.T("SnapshotDialog_Header", guestTitle, node);
        DataContext = _vm;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            var list = await _api.GetSnapshotsAsync(_node, _kind, _vmid);
            _vm.Snapshots.Clear();
            foreach (var snapshot in list) _vm.Snapshots.Add(snapshot);

            _vm.StatusText = list.Count == 0 ? Loc.T("SnapshotDialog_Empty") : string.Empty;
        }
        catch (Exception ex)
        {
            _vm.StatusText = Loc.T("SnapshotDialog_LoadFailed", ex.Message);
        }
    }

    private async Task RunTaskAsync(Func<Task<string>> startOperation, string label)
    {
        if (_busy) return;

        _busy = true;
        try
        {
            _vm.StatusText = Loc.T("SnapshotDialog_Running", label);
            var upid = await startOperation();
            var task = await _api.WaitTaskAsync(upid);
            _vm.StatusText = task.IsOk
                ? Loc.T("SnapshotDialog_Done", label)
                : Loc.T("SnapshotDialog_Failed", label, task.Status);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _vm.StatusText = Loc.T("SnapshotDialog_Failed", label, ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = _vm.NewName.Trim();
        if (name.Length == 0 || name.Contains(' '))
        {
            ThemedMessageBox.Show(this, Loc.T("SnapshotDialog_M01"), Loc.T("ProfileDialog_M08"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RunTaskAsync(
            () => _api.CreateSnapshotAsync(_node, _kind, _vmid, name, NullIfEmpty(_vm.NewDescription),
                _vm.NewIncludeRam),
            Loc.T("SnapshotDialog_ActionCreate", name));
        _vm.NewName = string.Empty;
        _vm.NewDescription = string.Empty;
        _vm.NewIncludeRam = false;
    }

    private async void OnRollback(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSnapshot is not { } snapshot)
        {
            ThemedMessageBox.Show(this, Loc.T("SnapshotDialog_M02"), Loc.T("MainWindow_M02"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var answer = ThemedMessageBox.Show(this,
            Loc.T("SnapshotDialog_M03", snapshot.Name),
            Loc.T("SnapshotDialog_M04"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        await RunTaskAsync(
            () => _api.RollbackSnapshotAsync(_node, _kind, _vmid, snapshot.Name),
            Loc.T("SnapshotDialog_ActionRollback", snapshot.Name));
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSnapshot is not { } snapshot)
        {
            ThemedMessageBox.Show(this, Loc.T("SnapshotDialog_M05"), Loc.T("MainWindow_M02"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var answer = ThemedMessageBox.Show(this, Loc.T("SnapshotDialog_M06", snapshot.Name),
            Loc.T("FirewallWindow_M08"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        await RunTaskAsync(
            () => _api.DeleteSnapshotAsync(_node, _kind, _vmid, snapshot.Name),
            Loc.T("SnapshotDialog_ActionDelete", snapshot.Name));
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
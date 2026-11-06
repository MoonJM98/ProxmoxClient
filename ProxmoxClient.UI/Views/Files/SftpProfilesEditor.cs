using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.Core.Files;
using ProxmoxClient.Core.Vnc;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Views.Controls;
using ProxmoxClient.UI.Views.Shared;
using ProxmoxClient.UI.Views.Storage;
using static ProxmoxClient.Presentation.Files.SftpProfileRules;

namespace ProxmoxClient.UI.Views.Files;

/// <summary>
///     SFTP 접속·계정 편집 — WPF SftpProfilesEditor 와 같은 두 탭(접속 · 계정)과 같은 칸·문구·저장 규칙.
///     데스크톱은 WPF 와 같이 목록(왼쪽 230) + 입력(오른쪽), 휴대폰은 목록을 위에 두고 입력을 아래에 쌓는다.
///     키 파일은 데스크톱에서 그 경로를 그대로, 안드로이드에서는 앱 폴더로 복사해 둔 경로를 쓴다(<see cref="KeyFiles" />).
/// </summary>
internal sealed class SftpProfilesEditor : UserControl
{
    private sealed record AuthenticationOption(SftpAuthenticationMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record SessionSecret(SftpAccountProfile Account, string Value);

    private readonly SftpProfileStore _store = new();
    private readonly TabControl _tabs = new();
    private readonly ListBox _connectionList = new();
    private readonly ListBox _accountList = new();
    private readonly TextBox _connectionName = new();
    private readonly TextBox _hostBox = new();
    private readonly TextBox _portBox = new() { Text = "22" };
    private readonly ComboBox _accountChoice = new();
    private readonly TextBox _accountName = new();
    private readonly TextBox _userBox = new();
    private readonly ComboBox _authentication = new();
    private readonly TextBox _privateKeyBox = new();
    private readonly TextBox _certificateBox = new();
    private readonly TextBlock _defaultKeysHint = new() { Text = Loc.T("Sftp_DefaultKeysHint"), IsVisible = false, TextWrapping = TextWrapping.Wrap, Classes = { "subtle" } };
    private readonly StackPanel _privateKeyPanel = new();
    private readonly StackPanel _certificatePanel = new();
    private readonly TextBlock _secretLabel = new() { Classes = { "label" } };
    private readonly TextBox _passwordBox = new() { PasswordChar = '•' };
    private readonly TextBlock _secretHint = new() { TextWrapping = TextWrapping.Wrap, Classes = { "subtle" } };
    private readonly CheckBox _rememberBox = new();
    private readonly TextBlock _passwordState = new() { Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _forgetSecret = new();
    private readonly Button _useConnection = new() { Classes = { "accent" } };
    private readonly Button _keySetup = new();
    private readonly TextBlock _status = new() { Classes = { "DialogStatus" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<Guid, SessionSecret> _sessionSecrets = [];
    private readonly AuthenticationOption[] _authOptions;
    private ConsoleSettings _settings = new();
    private Guid _connectionId;
    private Guid _accountId;
    private bool _loading;
    private bool _busy;

    public SftpProfilesEditor(Guid? selectedProfileId, bool allowConnect)
    {
        _connectionId = selectedProfileId ?? Guid.NewGuid();
        _accountId = Guid.NewGuid();
        _authOptions =
        [
            new AuthenticationOption(SftpAuthenticationMode.Password, Loc.T("Sftp_AuthPassword")),
            new AuthenticationOption(SftpAuthenticationMode.PrivateKey, Loc.T("Sftp_AuthPrivateKey")),
            new AuthenticationOption(SftpAuthenticationMode.DefaultKeys, Loc.T("Sftp_AuthDefaultKeys")),
            new AuthenticationOption(SftpAuthenticationMode.Certificate, Loc.T("Sftp_AuthCertificate"))
        ];
        _authentication.ItemsSource = _authOptions;
        _authentication.SelectedIndex = 0;

        _tabs.Items.Add(new TabItem { Header = Loc.T("Sftp_Connections"), Content = ConnectionsTab() });
        _tabs.Items.Add(new TabItem { Header = Loc.T("Sftp_Accounts"), Content = AccountsTab() });
        _tabs.SelectionChanged += (_, _) => UpdateUseButton();
        _useConnection.Content = Loc.T("Sftp_UseForGuest");
        _useConnection.IsVisible = allowConnect;
        _useConnection.Click += async (_, _) => await UseConnectionAsync();
        var close = new Button { Content = Loc.T("Sftp_Close"), Classes = { "DialogButton" }, MinWidth = 88, MinHeight = 40 };
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { _useConnection, close }
        };
        var grid = new Grid { RowDefinitions = RowDefinitions.Parse("*,Auto,Auto") };
        grid.Children.Add(new Border { Margin = new Thickness(PlatformInfo.IsTouch ? 8 : 16), Child = _tabs });
        Grid.SetRow(_status, 1);
        _status.Margin = new Thickness(20, 0, 20, 12);
        grid.Children.Add(_status);
        var border = new Border { Classes = { "DialogFooter" }, Child = footer };
        Grid.SetRow(border, 2);
        grid.Children.Add(border);
        Content = grid;

        Loaded += async (_, _) => await RunAsync(async () =>
        {
            await RefreshAsync();
            if (!_settings.SftpConnections.ContainsKey(_connectionId) && _settings.SftpConnections.Count > 0)
                _connectionId = _settings.SftpConnections.Values.First().Id;
            FillConnection();
            FillAccount();
        });
        Unloaded += (_, _) =>
        {
            _passwordBox.Text = string.Empty;
            _sessionSecrets.Clear();
        };
    }

    /// <summary>접속 선택 — 페이지가 결과를 받아 닫는다.</summary>
    public event EventHandler? ConnectionSelected;

    /// <summary>닫기 단추 — 페이지가 닫는다.</summary>
    public event EventHandler? CloseRequested;

    public Guid? SelectedProfileId { get; private set; }

    public string? SelectedPassword { get; private set; }

    public SftpAccountProfile? SelectedAccount { get; private set; }

    private Control ConnectionsTab()
    {
        var add = new Button { Content = Loc.T("Sftp_Add"), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += (_, _) => { _connectionId = Guid.NewGuid(); FillConnection(); };
        var delete = new Button { Content = Loc.T("Sftp_Delete") };
        delete.Click += async (_, _) => await RunAsync(async () =>
        {
            await _store.DeleteConnectionAsync(_connectionId);
            _connectionId = Guid.NewGuid();
            await RefreshAsync();
            FillConnection();
        });
        _connectionList.SelectionChanged += (_, _) =>
        {
            if (_loading || _connectionList.SelectedItem is not SftpConnectionProfile profile) return;
            _connectionId = profile.Id;
            FillConnection();
        };
        var manage = new Button { Content = Loc.T("Sftp_ManageAccounts"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        manage.Click += (_, _) => ManageAccounts();
        var save = new Button { Content = Loc.T("Sftp_SaveProfile"), HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += async (_, _) => await RunAsync(async () =>
        {
            await SaveConnectionAsync();
            _status.Text = Loc.T("Sftp_Saved");
        });
        _accountChoice.HorizontalAlignment = HorizontalAlignment.Stretch;

        var form = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Label("Sftp_ProfileName"), _connectionName,
                Label("Sftp_Host"), _hostBox,
                Label("Sftp_Port"), _portBox,
                Label("Sftp_Account"), _accountChoice,
                manage, save
            }
        };
        return Split(_connectionList, [add, delete], form);
    }

    private Control AccountsTab()
    {
        var add = new Button { Content = Loc.T("Sftp_Add"), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += (_, _) =>
        {
            _accountId = Guid.NewGuid();
            _accountList.SelectedItem = null;
            FillAccount();
        };
        var delete = new Button { Content = Loc.T("Sftp_Delete") };
        delete.Click += async (_, _) => await RunAsync(async () =>
        {
            await _store.DeleteAccountAsync(_accountId);
            _sessionSecrets.Remove(_accountId);
            _accountId = Guid.NewGuid();
            await RefreshAsync();
            FillAccount();
        });
        _accountList.SelectionChanged += (_, _) =>
        {
            if (_loading || _accountList.SelectedItem is not SftpAccountProfile account) return;
            _accountId = account.Id;
            FillAccount();
        };

        _authentication.HorizontalAlignment = HorizontalAlignment.Stretch;
        _authentication.SelectionChanged += (_, _) => OnAuthenticationChanged();
        _keySetup.Content = Loc.T("Sftp_KeySetup");
        _keySetup.HorizontalAlignment = HorizontalAlignment.Left;
        _keySetup.Click += async (_, _) => await RunAsync(OpenKeySetupAsync);

        _privateKeyPanel.Children.Add(Label("Sftp_PrivateKeyPath"));
        _privateKeyPanel.Children.Add(PathRow(_privateKeyBox, false));
        _certificatePanel.Children.Add(Label("Sftp_CertificatePath"));
        _certificatePanel.Children.Add(PathRow(_certificateBox, true));
        _certificatePanel.Children.Add(new TextBlock { Text = Loc.T("Sftp_CertificateHint"), TextWrapping = TextWrapping.Wrap, Classes = { "subtle" }, Margin = new Thickness(0, 0, 0, 10) });
        _privateKeyPanel.IsVisible = false;
        _certificatePanel.IsVisible = false;

        _rememberBox.Content = Loc.T("Sftp_Remember");
        _forgetSecret.Content = Loc.T("Sftp_ForgetPassword");
        _forgetSecret.Click += async (_, _) => await RunAsync(async () =>
        {
            await _store.ForgetPasswordAsync(_accountId);
            _sessionSecrets.Remove(_accountId);
            await RefreshAsync();
            FillAccount();
        });
        var save = new Button { Content = Loc.T("Sftp_SaveProfile"), Margin = new Thickness(0, 0, 8, 0) };
        save.Click += async (_, _) => await RunAsync(SaveAccountAsync);

        var form = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Label("Sftp_ProfileName"), _accountName,
                Label("Sftp_User"), _userBox,
                Label("Sftp_Authentication"), _authentication,
                _keySetup,
                _privateKeyPanel, _certificatePanel, _defaultKeysHint,
                _secretLabel, _passwordBox,
                _secretHint,
                _rememberBox,
                _passwordState,
                new WrapPanel { Children = { save, _forgetSecret } }
            }
        };
        return Split(_accountList, [add, delete], form);
    }

    /// <summary>목록 + 단추 + 입력 칸 — 데스크톱은 나란히(WPF 와 같이), 휴대폰은 위아래로.</summary>
    private static Control Split(ListBox list, IReadOnlyList<Button> buttons, Control form)
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in buttons) bar.Children.Add(button);
        if (PlatformInfo.IsDesktop)
        {
            var left = new DockPanel { Width = 230 };
            DockPanel.SetDock(bar, Dock.Bottom);
            left.Children.Add(bar);
            left.Children.Add(list);
            var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("230,20,*"), Margin = new Thickness(10) };
            grid.Children.Add(left);
            Grid.SetColumn(form, 2);
            grid.Children.Add(form);
            return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        }

        list.Height = 170;
        list.Classes.Add("cards");
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Margin = new Thickness(4), Spacing = 6, Children = { list, bar, form } }
        };
    }

    private static TextBlock Label(string key) => new() { Text = Loc.T(key), Classes = { "label" }, Margin = new Thickness(2, 6, 0, 4) };

    private Control PathRow(TextBox box, bool certificate)
    {
        var browse = new Button { Content = Loc.T("Sftp_Browse"), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        browse.Click += async (_, _) => await BrowsePathAsync(box, certificate);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(browse);
        row.Children.Add(box);
        return row;
    }

    private void UpdateUseButton()
    {
        _useConnection.IsEnabled = _tabs.SelectedIndex == 0 && !_busy;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _tabs.IsEnabled = false;
        UpdateUseButton();
        _status.Text = string.Empty;
        try { await action(); }
        catch (Exception ex) { _status.Text = Loc.T(ex.Message); }
        finally
        {
            _tabs.IsEnabled = true;
            _busy = false;
            UpdateUseButton();
        }
    }

    private async Task RefreshAsync()
    {
        var chosenAccount = (_accountChoice.SelectedItem as SftpAccountProfile)?.Id;
        var settings = await _store.LoadAsync();
        _settings = settings;
        _loading = true;
        try
        {
            _connectionList.ItemsSource = settings.SftpConnections.Values.OrderBy(p => p.Name).ToArray();
            _connectionList.SelectedItem = settings.SftpConnections.GetValueOrDefault(_connectionId);
            _accountList.ItemsSource = settings.SftpAccounts.Values.OrderBy(p => p.Name).ToArray();
            _accountList.SelectedItem = settings.SftpAccounts.GetValueOrDefault(_accountId);
            _accountChoice.ItemsSource = settings.SftpAccounts.Values.OrderBy(p => p.Name).ToArray();
            _accountChoice.SelectedItem = chosenAccount is { } id ? settings.SftpAccounts.GetValueOrDefault(id) : null;
        }
        finally { _loading = false; }
    }

    private void FillConnection()
    {
        var settings = _settings;
        var profile = settings.SftpConnections.GetValueOrDefault(_connectionId) ?? new SftpConnectionProfile { Id = _connectionId };
        _connectionName.Text = profile.Name;
        _hostBox.Text = profile.Host;
        _portBox.Text = profile.Port.ToString(CultureInfo.InvariantCulture);
        _loading = true;
        try
        {
            _accountChoice.SelectedItem = settings.SftpAccounts.GetValueOrDefault(profile.AccountId);
            _connectionList.SelectedItem = settings.SftpConnections.GetValueOrDefault(_connectionId);
        }
        finally { _loading = false; }
    }

    private void FillAccount()
    {
        var account = _settings.SftpAccounts.GetValueOrDefault(_accountId) ?? new SftpAccountProfile { Id = _accountId };
        _loading = true;
        try
        {
            _accountName.Text = account.Name;
            _userBox.Text = account.UserName;
            SelectAuthentication(account.AuthenticationMode);
            _privateKeyBox.Text = account.PrivateKeyPath;
            _certificateBox.Text = account.CertificatePath;
            _rememberBox.IsChecked = account.RememberSecret;
            _passwordBox.Text = string.Empty;
            UpdateAuthenticationControls(account);
        }
        finally { _loading = false; }
    }

    private void SelectAuthentication(SftpAuthenticationMode mode)
    {
        var index = Array.FindIndex(_authOptions, o => o.Mode == mode);
        _authentication.SelectedIndex = index < 0 ? 0 : index;
    }

    private SftpAuthenticationMode CurrentAuthentication =>
        _authentication.SelectedItem is AuthenticationOption option ? option.Mode : SftpAuthenticationMode.Password;

    private void UpdateAuthenticationControls(SftpAccountProfile? account = null)
    {
        var mode = CurrentAuthentication;
        var usesKey = mode != SftpAuthenticationMode.Password;
        _privateKeyPanel.IsVisible = mode is SftpAuthenticationMode.PrivateKey or SftpAuthenticationMode.Certificate;
        _certificatePanel.IsVisible = mode == SftpAuthenticationMode.Certificate;
        _defaultKeysHint.IsVisible = mode == SftpAuthenticationMode.DefaultKeys;
        _secretLabel.Text = Loc.T(usesKey ? "Sftp_Passphrase" : "Sftp_Password");
        _secretHint.Text = Loc.T(usesKey ? "Sftp_PassphraseHint" : "Sftp_AccountPasswordHint");
        _rememberBox.Content = Loc.T(usesKey ? "Sftp_RememberPassphrase" : "Sftp_Remember");
        _forgetSecret.Content = Loc.T(usesKey ? "Sftp_ForgetPassphrase" : "Sftp_ForgetPassword");
        var stored = account ?? _settings.SftpAccounts.GetValueOrDefault(_accountId);
        var saved = stored is not null && stored.AuthenticationMode == mode && GuestSecrets.Read(stored.SecretTarget) is not null;
        _passwordState.Text = Loc.T(usesKey
            ? saved ? "Sftp_HasSavedPassphrase" : "Sftp_NoSavedPassphrase"
            : saved ? "Sftp_HasSavedPassword" : "Sftp_NoSavedPassword");
        _forgetSecret.IsEnabled = saved;
    }

    private void OnAuthenticationChanged()
    {
        if (_loading) return;
        _passwordBox.Text = string.Empty;
        _sessionSecrets.Remove(_accountId);
        var account = _settings.SftpAccounts.GetValueOrDefault(_accountId);
        _rememberBox.IsChecked = account is not null && account.AuthenticationMode == CurrentAuthentication && account.RememberSecret;
        UpdateAuthenticationControls(account);
    }

    private void ManageAccounts()
    {
        if (_accountChoice.SelectedItem is SftpAccountProfile account)
        {
            _accountId = account.Id;
            _accountList.SelectedItem = account;
            FillAccount();
        }
        _tabs.SelectedIndex = 1;
    }

    private async Task BrowsePathAsync(TextBox target, bool certificate)
    {
        var file = await StorageFiles.PickUploadAsync(this,
            Loc.T(certificate ? "Sftp_CertificateFilter" : "Sftp_KeyFilter"),
            Loc.T(certificate ? "Sftp_CertificatePath" : "Sftp_PrivateKeyPath"));
        if (file is null) return;

        target.Text = await KeyFiles.LocalPathAsync(file);
    }

    private async Task OpenKeySetupAsync()
    {
        var mode = CurrentAuthentication;
        var path = mode is SftpAuthenticationMode.PrivateKey or SftpAuthenticationMode.Certificate
            ? _privateKeyBox.Text ?? string.Empty : string.Empty;
        var passphrase = mode != SftpAuthenticationMode.Password && _passwordBox.Text is { Length: > 0 } typed
            ? typed : null;
        var result = await SshKeySetupWindow.ShowAsync(path, passphrase, _userBox.Text?.Trim() ?? string.Empty,
            mode == SftpAuthenticationMode.Certificate, mode == SftpAuthenticationMode.DefaultKeys);
        if (string.IsNullOrWhiteSpace(result.GeneratedPrivateKeyPath)) return;

        _sessionSecrets.Remove(_accountId);
        _passwordBox.Text = string.Empty;
        if (mode != SftpAuthenticationMode.Certificate)
            SelectAuthentication(SftpAuthenticationMode.PrivateKey);
        else
        {
            _certificateBox.Text = string.Empty;
            _status.Text = Loc.T("SshSetup_NewCertificateRequired");
        }
        _privateKeyBox.Text = result.GeneratedPrivateKeyPath;
        if (result.GeneratedPassphrase is { } generatedPassphrase) _passwordBox.Text = generatedPassphrase;
    }

    private async Task SaveConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(_connectionName.Text) || string.IsNullOrWhiteSpace(_hostBox.Text))
            throw new InvalidOperationException("Sftp_RequiredFields");
        if (!int.TryParse(_portBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("Sftp_BadPort");
        if (_accountChoice.SelectedItem is not SftpAccountProfile account)
            throw new InvalidOperationException("Sftp_SelectAccount");
        await _store.SaveConnectionAsync(new SftpConnectionProfile
        {
            Id = _connectionId, Name = _connectionName.Text ?? string.Empty, Host = _hostBox.Text ?? string.Empty,
            Port = port, AccountId = account.Id
        }.Normalize());
        await RefreshAsync();
        FillConnection();
    }

    private async Task SaveAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(_accountName.Text) || string.IsNullOrWhiteSpace(_userBox.Text))
            throw new InvalidOperationException("Sftp_RequiredFields");
        var mode = CurrentAuthentication;
        var password = _passwordBox.Text ?? string.Empty;
        if (password.Length > 1280)
            throw new InvalidOperationException(mode == SftpAuthenticationMode.Password ? "Sftp_PasswordLong" : "Sftp_PassphraseLong");
        if ((mode is SftpAuthenticationMode.PrivateKey or SftpAuthenticationMode.Certificate) && string.IsNullOrWhiteSpace(_privateKeyBox.Text))
            throw new InvalidOperationException("Sftp_KeyPathRequired");
        if (mode == SftpAuthenticationMode.Certificate && string.IsNullOrWhiteSpace(_certificateBox.Text))
            throw new InvalidOperationException("Sftp_CertificatePathRequired");
        var old = _settings.SftpAccounts.GetValueOrDefault(_accountId);
        var account = ((old ?? new SftpAccountProfile()) with
        {
            Id = _accountId, Name = _accountName.Text ?? string.Empty, UserName = _userBox.Text ?? string.Empty,
            AuthenticationMode = mode,
            PrivateKeyPath = mode is SftpAuthenticationMode.PrivateKey or SftpAuthenticationMode.Certificate ? LocalKeyPath(_privateKeyBox.Text ?? string.Empty) : string.Empty,
            CertificatePath = mode == SftpAuthenticationMode.Certificate ? LocalKeyPath(_certificateBox.Text ?? string.Empty) : string.Empty,
            RememberPassword = mode == SftpAuthenticationMode.Password && _rememberBox.IsChecked == true,
            RememberPassphrase = mode != SftpAuthenticationMode.Password && _rememberBox.IsChecked == true
        }).Normalize();
        if ((mode is SftpAuthenticationMode.PrivateKey or SftpAuthenticationMode.Certificate) && !File.Exists(account.PrivateKeyPath))
            throw new InvalidOperationException("Sftp_KeyFileMissing");
        if (mode == SftpAuthenticationMode.Certificate && !File.Exists(account.CertificatePath))
            throw new InvalidOperationException("Sftp_CertificateFileMissing");
        var secret = password.Length > 0 ? password : null;
        var retained = _sessionSecrets.GetValueOrDefault(account.Id);
        var canRetain = old is not null && retained is not null
            && SameAuthentication(old, account) && SameAuthentication(retained.Account, old);
        var saved = await _store.SaveAccountAsync(account, secret);
        var savedAccount = saved.SftpAccounts[account.Id];
        _sessionSecrets.Remove(account.Id);
        await RefreshAsync();
        if (!savedAccount.RememberSecret && _settings.SftpAccounts.TryGetValue(account.Id, out var current)
            && SameAuthentication(savedAccount, current))
        {
            if (secret is not null) _sessionSecrets[account.Id] = new SessionSecret(savedAccount, secret);
            else if (canRetain) _sessionSecrets[account.Id] = new SessionSecret(savedAccount, retained!.Value);
        }
        FillAccount();
        _accountChoice.SelectedItem ??= _settings.SftpAccounts.GetValueOrDefault(account.Id);
        _status.Text = Loc.T("Sftp_Saved");
    }

    private async Task UseConnectionAsync()
    {
        if (_tabs.SelectedIndex != 0)
        {
            _tabs.SelectedIndex = 0;
            return;
        }

        await RunAsync(async () =>
        {
            await SaveConnectionAsync();
            SelectedProfileId = _connectionId;
            var account = _settings.SftpAccounts[_settings.SftpConnections[_connectionId].AccountId];
            SelectedAccount = account;
            SelectedPassword = _sessionSecrets.TryGetValue(account.Id, out var secret) && SameAuthentication(secret.Account, account)
                ? secret.Value : null;
            ConnectionSelected?.Invoke(this, EventArgs.Empty);
        });
    }
}

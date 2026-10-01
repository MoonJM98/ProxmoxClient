using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.Core.Files;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.App.Views.Shared;

public partial class SftpProfilesEditor : UserControl
{
    private readonly SftpProfileStore _store = new();
    private ConsoleSettings _settings = new();
    private Guid _connectionId;
    private Guid _accountId;
    private bool _loading;
    private bool _busy;
    // 기억하지 않는 비밀 값은 이 대화상자에서 같은 인증 정보로 연결할 때까지만 보관한다.
    private readonly Dictionary<Guid, SessionSecret> _sessionSecrets = [];
    private sealed record AuthenticationOption(SftpAuthenticationMode Mode, string Label);
    private sealed record SessionSecret(SftpAccountProfile Account, string Value);
    public Guid? SelectedProfileId { get; private set; }
    internal string? SelectedPassword { get; private set; }
    internal SftpAccountProfile? SelectedAccount { get; private set; }

    public SftpProfilesEditor() : this(null, false) { }

    public event EventHandler? ConnectionSelected;

    public SftpProfilesEditor(Guid? selectedProfileId, bool allowConnect)
    {
        InitializeComponent();
        AuthenticationChoice.ItemsSource = new[]
        {
            new AuthenticationOption(SftpAuthenticationMode.Password, Loc.T("Sftp_AuthPassword")),
            new AuthenticationOption(SftpAuthenticationMode.PrivateKey, Loc.T("Sftp_AuthPrivateKey")),
            new AuthenticationOption(SftpAuthenticationMode.DefaultKeys, Loc.T("Sftp_AuthDefaultKeys")),
            new AuthenticationOption(SftpAuthenticationMode.Certificate, Loc.T("Sftp_AuthCertificate"))
        };
        AuthenticationChoice.SelectedValue = SftpAuthenticationMode.Password;
        ActionFooter.Visibility = allowConnect ? Visibility.Visible : Visibility.Collapsed;
        _connectionId = selectedProfileId ?? Guid.NewGuid();
        _accountId = Guid.NewGuid();
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            await RefreshAsync();
            if (!_settings.SftpConnections.ContainsKey(_connectionId) && _settings.SftpConnections.Count > 0)
                _connectionId = _settings.SftpConnections.Values.First().Id;
            FillConnection();
            FillAccount();
        });
        Unloaded += (_, _) => { PasswordBox.Clear(); _sessionSecrets.Clear(); };
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        Tabs.IsEnabled = false;
        StatusText.Text = string.Empty;
        try { await action(); }
        catch (Exception ex) { StatusText.Text = Loc.T(ex.Message); }
        finally { Tabs.IsEnabled = true; _busy = false; }
    }

    private async Task RefreshAsync()
    {
        var chosenAccount = (AccountChoice.SelectedItem as SftpAccountProfile)?.Id;
        _settings = await _store.LoadAsync();
        _loading = true;
        try
        {
            ConnectionList.ItemsSource = _settings.SftpConnections.Values.OrderBy(p => p.Name).ToArray();
            ConnectionList.SelectedItem = _settings.SftpConnections.GetValueOrDefault(_connectionId);
            AccountList.ItemsSource = _settings.SftpAccounts.Values.OrderBy(p => p.Name).ToArray();
            AccountList.SelectedItem = _settings.SftpAccounts.GetValueOrDefault(_accountId);
            AccountChoice.ItemsSource = _settings.SftpAccounts.Values.OrderBy(p => p.Name).ToArray();
            AccountChoice.SelectedItem = chosenAccount is { } id ? _settings.SftpAccounts.GetValueOrDefault(id) : null;
        }
        finally { _loading = false; }
    }

    private void FillConnection()
    {
        var profile = _settings.SftpConnections.GetValueOrDefault(_connectionId) ?? new SftpConnectionProfile { Id = _connectionId };
        ConnectionName.Text = profile.Name;
        HostBox.Text = profile.Host;
        PortBox.Text = profile.Port.ToString(CultureInfo.InvariantCulture);
        AccountChoice.SelectedItem = _settings.SftpAccounts.GetValueOrDefault(profile.AccountId);
        _loading = true;
        ConnectionList.SelectedItem = _settings.SftpConnections.GetValueOrDefault(_connectionId);
        _loading = false;
    }

    private void FillAccount()
    {
        var account = _settings.SftpAccounts.GetValueOrDefault(_accountId) ?? new SftpAccountProfile { Id = _accountId };
        _loading = true;
        try
        {
            AccountName.Text = account.Name;
            UserBox.Text = account.UserName;
            AuthenticationChoice.SelectedValue = account.AuthenticationMode;
            PrivateKeyBox.Text = account.PrivateKeyPath;
            CertificateBox.Text = account.CertificatePath;
            RememberBox.IsChecked = account.RememberSecret;
            PasswordBox.Clear();
            UpdateAuthenticationControls(account);
        }
        finally { _loading = false; }
    }

    private SftpAuthenticationMode CurrentAuthentication => AuthenticationChoice.SelectedValue is SftpAuthenticationMode mode
        ? mode : SftpAuthenticationMode.Password;

    private void UpdateAuthenticationControls(SftpAccountProfile? account = null)
    {
        var mode = CurrentAuthentication;
        var usesKey = mode != SftpAuthenticationMode.Password;
        PrivateKeyPanel.Visibility = mode is SftpAuthenticationMode.PrivateKey or SftpAuthenticationMode.Certificate
            ? Visibility.Visible : Visibility.Collapsed;
        CertificatePanel.Visibility = mode == SftpAuthenticationMode.Certificate ? Visibility.Visible : Visibility.Collapsed;
        DefaultKeysHint.Visibility = mode == SftpAuthenticationMode.DefaultKeys ? Visibility.Visible : Visibility.Collapsed;
        SecretLabel.Text = Loc.T(usesKey ? "Sftp_Passphrase" : "Sftp_Password");
        SecretHint.Text = Loc.T(usesKey ? "Sftp_PassphraseHint" : "Sftp_AccountPasswordHint");
        RememberBox.Content = Loc.T(usesKey ? "Sftp_RememberPassphrase" : "Sftp_Remember");
        ForgetSecretButton.Content = Loc.T(usesKey ? "Sftp_ForgetPassphrase" : "Sftp_ForgetPassword");
        var stored = account ?? _settings.SftpAccounts.GetValueOrDefault(_accountId);
        var saved = stored is not null && stored.AuthenticationMode == mode && WindowsCredentialStore.Read(stored.SecretTarget) is not null;
        PasswordState.Text = Loc.T(usesKey
            ? saved ? "Sftp_HasSavedPassphrase" : "Sftp_NoSavedPassphrase"
            : saved ? "Sftp_HasSavedPassword" : "Sftp_NoSavedPassword");
        ForgetSecretButton.IsEnabled = saved;
    }

    private void OnAuthenticationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        PasswordBox.Clear();
        _sessionSecrets.Remove(_accountId);
        var account = _settings.SftpAccounts.GetValueOrDefault(_accountId);
        RememberBox.IsChecked = account is not null && account.AuthenticationMode == CurrentAuthentication && account.RememberSecret;
        UpdateAuthenticationControls(account);
    }

    private void BrowsePath(TextBox target, bool certificate)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T(certificate ? "Sftp_CertificatePath" : "Sftp_PrivateKeyPath"),
            Filter = Loc.T(certificate ? "Sftp_CertificateFilter" : "Sftp_KeyFilter"),
            CheckFileExists = true,
            Multiselect = false
        };
        var sshDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (Directory.Exists(sshDirectory)) dialog.InitialDirectory = sshDirectory;
        if (File.Exists(target.Text)) dialog.FileName = target.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) target.Text = dialog.FileName;
    }
    private void OnBrowsePrivateKey(object sender, RoutedEventArgs e) => BrowsePath(PrivateKeyBox, false);
    private void OnBrowseCertificate(object sender, RoutedEventArgs e) => BrowsePath(CertificateBox, true);

    private async void OnKeySetup(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        var mode = CurrentAuthentication;
        var path = mode is SftpAuthenticationMode.PrivateKey or SftpAuthenticationMode.Certificate
            ? PrivateKeyBox.Text : string.Empty;
        var passphrase = mode != SftpAuthenticationMode.Password && PasswordBox.Password.Length > 0
            ? PasswordBox.Password : null;
        var dialog = new SshKeySetupWindow(path, passphrase, UserBox.Text.Trim(),
            mode == SftpAuthenticationMode.Certificate, mode == SftpAuthenticationMode.DefaultKeys)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
        if (!string.IsNullOrWhiteSpace(dialog.GeneratedPrivateKeyPath))
        {
            _sessionSecrets.Remove(_accountId);
            PasswordBox.Clear();
            if (mode != SftpAuthenticationMode.Certificate)
                AuthenticationChoice.SelectedValue = SftpAuthenticationMode.PrivateKey;
            else
            {
                CertificateBox.Clear();
                StatusText.Text = Loc.T("SshSetup_NewCertificateRequired");
            }
            PrivateKeyBox.Text = dialog.GeneratedPrivateKeyPath;
            if (dialog.GeneratedPassphrase is { } generatedPassphrase) PasswordBox.Password = generatedPassphrase;
            PrivateKeyBox.Focus();
        }
        return Task.CompletedTask;
    });

    private static string LocalKeyPath(string input)
    {
        var path = Environment.ExpandEnvironmentVariables(input.Trim());
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return Path.GetFullPath(path);
    }

    private static bool SameAuthentication(SftpAccountProfile old, SftpAccountProfile current) =>
        old.UserName == current.UserName && old.AuthenticationMode == current.AuthenticationMode &&
        old.PrivateKeyPath == current.PrivateKeyPath && old.CertificatePath == current.CertificatePath &&
        old.CredentialRevision == current.CredentialRevision;

    private void OnConnectionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ConnectionList.SelectedItem is not SftpConnectionProfile profile) return;
        _connectionId = profile.Id;
        FillConnection();
    }
    private void OnAccountSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AccountList.SelectedItem is not SftpAccountProfile account) return;
        _accountId = account.Id;
        FillAccount();
    }
    private void OnNewConnection(object sender, RoutedEventArgs e)
    {
        _connectionId = Guid.NewGuid();
        FillConnection();
    }
    private void OnNewAccount(object sender, RoutedEventArgs e)
    {
        _accountId = Guid.NewGuid();
        AccountList.SelectedItem = null;
        FillAccount();
    }
    private void OnManageAccounts(object sender, RoutedEventArgs e)
    {
        if (AccountChoice.SelectedItem is SftpAccountProfile account)
        {
            _accountId = account.Id;
            AccountList.SelectedItem = account;
            FillAccount();
        }
        Tabs.SelectedIndex = 1;
    }

    private async Task SaveConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionName.Text) || string.IsNullOrWhiteSpace(HostBox.Text))
            throw new InvalidOperationException("Sftp_RequiredFields");
        if (!int.TryParse(PortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("Sftp_BadPort");
        if (AccountChoice.SelectedItem is not SftpAccountProfile account)
            throw new InvalidOperationException("Sftp_SelectAccount");
        await _store.SaveConnectionAsync(new SftpConnectionProfile
        {
            Id = _connectionId, Name = ConnectionName.Text, Host = HostBox.Text, Port = port, AccountId = account.Id
        }.Normalize());
        await RefreshAsync();
        FillConnection();
    }
    private async void OnSaveConnection(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await SaveConnectionAsync();
        StatusText.Text = Loc.T("Sftp_Saved");
    });
    private async void OnSaveAccount(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(AccountName.Text) || string.IsNullOrWhiteSpace(UserBox.Text))
            throw new InvalidOperationException("Sftp_RequiredFields");
        var mode = CurrentAuthentication;
        if (PasswordBox.Password.Length > 1280)
            throw new InvalidOperationException(mode == SftpAuthenticationMode.Password ? "Sftp_PasswordLong" : "Sftp_PassphraseLong");
        if ((mode is SftpAuthenticationMode.PrivateKey or SftpAuthenticationMode.Certificate) && string.IsNullOrWhiteSpace(PrivateKeyBox.Text))
            throw new InvalidOperationException("Sftp_KeyPathRequired");
        if (mode == SftpAuthenticationMode.Certificate && string.IsNullOrWhiteSpace(CertificateBox.Text))
            throw new InvalidOperationException("Sftp_CertificatePathRequired");
        var old = _settings.SftpAccounts.GetValueOrDefault(_accountId);
        var account = ((old ?? new SftpAccountProfile()) with
        {
            Id = _accountId, Name = AccountName.Text, UserName = UserBox.Text,
            AuthenticationMode = mode,
            PrivateKeyPath = mode is SftpAuthenticationMode.PrivateKey or SftpAuthenticationMode.Certificate ? LocalKeyPath(PrivateKeyBox.Text) : string.Empty,
            CertificatePath = mode == SftpAuthenticationMode.Certificate ? LocalKeyPath(CertificateBox.Text) : string.Empty,
            RememberPassword = mode == SftpAuthenticationMode.Password && RememberBox.IsChecked == true,
            RememberPassphrase = mode != SftpAuthenticationMode.Password && RememberBox.IsChecked == true
        }).Normalize();
        if ((mode is SftpAuthenticationMode.PrivateKey or SftpAuthenticationMode.Certificate) && !File.Exists(account.PrivateKeyPath))
            throw new InvalidOperationException("Sftp_KeyFileMissing");
        if (mode == SftpAuthenticationMode.Certificate && !File.Exists(account.CertificatePath))
            throw new InvalidOperationException("Sftp_CertificateFileMissing");
        var secret = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;
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
            if (secret is not null) _sessionSecrets[account.Id] = new(savedAccount, secret);
            else if (canRetain) _sessionSecrets[account.Id] = new(savedAccount, retained!.Value);
        }
        FillAccount();
        AccountChoice.SelectedItem ??= _settings.SftpAccounts.GetValueOrDefault(account.Id);
        StatusText.Text = Loc.T("Sftp_Saved");
    });
    private async void OnDeleteAccount(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await _store.DeleteAccountAsync(_accountId);
        _sessionSecrets.Remove(_accountId);
        _accountId = Guid.NewGuid();
        await RefreshAsync();
        FillAccount();
    });
    private async void OnDeleteConnection(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await _store.DeleteConnectionAsync(_connectionId);
        _connectionId = Guid.NewGuid();
        await RefreshAsync();
        FillConnection();
    });
    private async void OnForgetPassword(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await _store.ForgetPasswordAsync(_accountId);
        _sessionSecrets.Remove(_accountId);
        await RefreshAsync();
        FillAccount();
    });
    private async void OnUseConnection(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedIndex != 0) { Tabs.SelectedIndex = 0; return; }
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


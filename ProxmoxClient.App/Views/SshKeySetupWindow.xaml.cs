using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Files;

namespace ProxmoxClient.App.Views;

public partial class SshKeySetupWindow : Window
{
    private sealed record KeyTypeOption(SshKeyType Type, string Label);
    private readonly CancellationTokenSource _cancel = new();
    private readonly bool _certificateMode;
    private string? _passphrase;
    private string? _loadedKeyPath;
    private string _pasteCommand = string.Empty;
    private bool _busy;
    private bool _closed;
    private string? _suggestedPath;
    public string? GeneratedPrivateKeyPath { get; private set; }
    public string? GeneratedPassphrase { get; private set; }

    public SshKeySetupWindow(string privateKeyPath, string? passphrase, string userName,
        bool certificateMode, bool defaultKeysMode)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _passphrase = passphrase;
        _certificateMode = certificateMode;
        KeyTypeChoice.ItemsSource = new[]
        {
            new KeyTypeOption(SshKeyType.Rsa3072, Loc.T("SshSetup_Rsa3072")),
            new KeyTypeOption(SshKeyType.Ed25519, Loc.T("SshSetup_Ed25519"))
        };
        KeyTypeChoice.SelectedValue = SshKeyType.Rsa3072;
        CommentBox.Text = userName.Length > 0 ? $"{userName}@ProxmoxClient" : "ProxmoxClient";
        var paths = !defaultKeysMode && !string.IsNullOrWhiteSpace(privateKeyPath)
            ? new List<string> { privateKeyPath } : SshKeySetup.DiscoverDefaultKeys().ToList();
        ExistingKeyChoice.ItemsSource = paths;
        ExistingKeyChoice.Text = !defaultKeysMode && privateKeyPath.Length > 0 ? privateKeyPath : paths.FirstOrDefault() ?? string.Empty;
        if (ExistingKeyChoice.Text.Length == 0) SetupTabs.SelectedIndex = 1;
        RegistrationDescription.Text = Loc.T(certificateMode ? "SshSetup_CertificateRegistration" : "SshSetup_RegistrationDescription",
            userName.Length > 0 ? userName : Loc.T("SshSetup_TargetUser"));
        RegistrationCommands.Visibility = certificateMode ? Visibility.Collapsed : Visibility.Visible;
        CertificateDescription.Visibility = certificateMode ? Visibility.Visible : Visibility.Collapsed;
        ExistingKeyChoice.SelectionChanged += (_, _) => InvalidatePublicKey();
        ExistingKeyChoice.AddHandler(TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => InvalidatePublicKey()));
        Closed += (_, _) =>
        {
            _closed = true;
            _cancel.Cancel();
            if (!_busy) _cancel.Dispose();
            _passphrase = null;
            NewPassphraseBox.Clear();
            ConfirmPassphraseBox.Clear();
        };
    }

    private static string LocalPath(string input)
    {
        var path = Environment.ExpandEnvironmentVariables(input.Trim());
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return Path.GetFullPath(path);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        SetupTabs.IsEnabled = false;
        StatusText.Text = Loc.T("SshSetup_Working");
        try { await action(); }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = Loc.T("GuestFiles_Cancelled"); }
        catch (Exception ex) { if (!_closed) StatusText.Text = Loc.T(ex.Message); }
        finally { SetupTabs.IsEnabled = true; _busy = false; if (_closed) _cancel.Dispose(); }
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Loc.T("Sftp_PrivateKeyPath"), Filter = Loc.T("Sftp_KeyFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) ExistingKeyChoice.Text = dialog.FileName;
    }

    private async void OnReadPublicKey(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        ClearPublicKey();
        if (string.IsNullOrWhiteSpace(ExistingKeyChoice.Text)) throw new InvalidOperationException(Loc.T("Sftp_KeyPathRequired"));
        var path = LocalPath(ExistingKeyChoice.Text);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var key = await Task.Run(() => SshKeySetup.ReadPublicKey(path, _passphrase), _cancel.Token);
                _cancel.Token.ThrowIfCancellationRequested();
                var ready = SetPublicKey(path, key);
                StatusText.Text = Loc.T(!ready ? "Sftp_RegistrationCommandLong"
                    : _certificateMode ? "SshSetup_CertificatePublicKeyReady" : "SshSetup_PublicKeyReady");
                return;
            }
            catch (GuestFileException ex) when ((ex is SftpPassphraseRequiredException or SftpPassphraseException) && attempt < 2)
            {
                _cancel.Token.ThrowIfCancellationRequested();
                var dialog = new FormDialog(Loc.T("SshSetup_UnlockKey"),
                [new() { Key = "passphrase", LabelKey = "Sftp_Passphrase", Kind = FormFieldKind.Password,
                    Hint = ex.Message }]) { Owner = this };
                if (dialog.ShowDialog() != true || dialog.Result is not { } result) throw new OperationCanceledException();
                _passphrase = result["passphrase"];
            }
        }
    });

    private bool SetPublicKey(string path, string publicKey)
    {
        var command = _certificateMode ? string.Empty : SshRegistrationGuide.CreateAuthorizedKeysCommand(publicKey);
        var pasteCommand = string.Empty;
        if (!_certificateMode)
        {
            try { pasteCommand = SshRegistrationGuide.CreatePasteCommand(publicKey); }
            catch (InvalidOperationException ex) when (ex.Message == "Sftp_RegistrationCommandLong") { }
        }
        _loadedKeyPath = path;
        _pasteCommand = pasteCommand;
        PublicKeyBox.Text = publicKey;
        CommandBox.Text = command;
        CopyPublicKeyButton.IsEnabled = SavePublicKeyButton.IsEnabled = true;
        CopyCommandButton.IsEnabled = pasteCommand.Length > 0;
        return _certificateMode || pasteCommand.Length > 0;
    }

    private void ClearPublicKey()
    {
        _loadedKeyPath = null;
        _pasteCommand = string.Empty;
        PublicKeyBox.Clear();
        CommandBox.Clear();
        CopyPublicKeyButton.IsEnabled = SavePublicKeyButton.IsEnabled = CopyCommandButton.IsEnabled = false;
    }
    private void InvalidatePublicKey()
    {
        if (_loadedKeyPath is null) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(ExistingKeyChoice.Text)
                && string.Equals(LocalPath(ExistingKeyChoice.Text), _loadedKeyPath, StringComparison.OrdinalIgnoreCase)) return;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        ClearPublicKey();
        if (!_busy) StatusText.Text = string.Empty;
    }

    private void Copy(string value)
    {
        try { Clipboard.SetText(value); StatusText.Text = Loc.T("GuestFiles_Copied"); }
        catch (ExternalException ex) { StatusText.Text = Loc.T("GuestFiles_Error", ex.Message); }
    }
    private void OnCopyPublicKey(object sender, RoutedEventArgs e) => Copy(PublicKeyBox.Text);
    private void OnCopyCommand(object sender, RoutedEventArgs e)
    {
        if (_pasteCommand.Length > 0) Copy(_pasteCommand);
    }

    private async void OnSavePublicKey(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("SshSetup_SavePublicKey"), FileName = (_loadedKeyPath ?? "id_ed25519") + ".pub",
            Filter = Loc.T("SshSetup_PublicKeyFilter"), OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true) { StatusText.Text = string.Empty; return; }
        var created = false;
        try
        {
            await using var output = new FileStream(dialog.FileName, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            await using var writer = new StreamWriter(output, new UTF8Encoding(false));
            await writer.WriteAsync((PublicKeyBox.Text + "\n").AsMemory(), _cancel.Token);
            await writer.FlushAsync(_cancel.Token);
        }
        catch
        {
            if (created) File.Delete(dialog.FileName);
            throw;
        }
        StatusText.Text = Loc.T("SshSetup_PublicKeySaved", dialog.FileName);
    });

    private void OnKeyTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KeyTypeChoice.SelectedValue is not SshKeyType type) return;
        var supportsPhrase = type == SshKeyType.Rsa3072;
        GenerationPassphrasePanel.Visibility = supportsPhrase ? Visibility.Visible : Visibility.Collapsed;
        Ed25519GenerationHint.Visibility = supportsPhrase ? Visibility.Collapsed : Visibility.Visible;
        if (!supportsPhrase) { NewPassphraseBox.Clear(); ConfirmPassphraseBox.Clear(); }
        if (NewKeyPathBox.Text.Length == 0 || NewKeyPathBox.Text == _suggestedPath)
        {
            _suggestedPath = SshKeySetup.DefaultPrivateKeyPath(type);
            NewKeyPathBox.Text = _suggestedPath;
        }
    }
    private void OnChooseNewPath(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = Loc.T("SshSetup_NewPath"), FileName = NewKeyPathBox.Text,
            Filter = Loc.T("Sftp_KeyFilter"), OverwritePrompt = false };
        if (dialog.ShowDialog(this) == true) NewKeyPathBox.Text = dialog.FileName;
    }
    private async void OnGenerate(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewKeyPathBox.Text)) throw new InvalidOperationException(Loc.T("Sftp_KeyPathRequired"));
        if (NewPassphraseBox.Password != ConfirmPassphraseBox.Password)
            throw new InvalidOperationException(Loc.T("SshSetup_PassphraseMismatch"));
        if (NewPassphraseBox.Password.Length > 1280) throw new InvalidOperationException(Loc.T("Sftp_PassphraseLong"));
        var type = KeyTypeChoice.SelectedValue is SshKeyType choice ? choice : SshKeyType.Rsa3072;
        var phrase = NewPassphraseBox.Password;
        var key = await SshKeySetup.GenerateAsync(LocalPath(NewKeyPathBox.Text), type, phrase, CommentBox.Text, _cancel.Token);
        _cancel.Token.ThrowIfCancellationRequested();
        GeneratedPrivateKeyPath = key.PrivateKeyPath;
        GeneratedPassphrase = phrase.Length > 0 ? phrase : null;
        _passphrase = GeneratedPassphrase;
        ExistingKeyChoice.Text = key.PrivateKeyPath;
        SetPublicKey(key.PrivateKeyPath, key.PublicKey);
        NewPassphraseBox.Clear();
        ConfirmPassphraseBox.Clear();
        SetupTabs.SelectedIndex = 0;
        StatusText.Text = Loc.T("SshSetup_Generated", key.PrivateKeyPath);
    });
    private void OnOpenDocs(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://man.openbsd.org/sshd.8#AUTHORIZED_KEYS_FILE_FORMAT") { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

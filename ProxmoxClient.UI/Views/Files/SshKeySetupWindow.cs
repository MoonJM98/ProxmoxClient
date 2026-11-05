using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ProxmoxClient.Core.Files;
using ProxmoxClient.Core.Vnc;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Controls;
using ProxmoxClient.UI.Views.Shared;
using ProxmoxClient.UI.Views.Storage;

namespace ProxmoxClient.UI.Views.Files;

/// <summary>SSH 키 설정 결과 — 새로 만든 키 경로와 암호(없으면 null). 만들지 않았으면 경로가 null.</summary>
public sealed record SshKeySetupResult(string? GeneratedPrivateKeyPath, string? GeneratedPassphrase);

/// <summary>
///     SSH 키 설정 — WPF SshKeySetupWindow 와 같은 두 탭(공개키 등록 · 새 키 만들기)과 같은 문구.
///     화면 스택의 페이지로 연다(대화상자 안에서 또 대화상자를 띄울 수 없으므로). 닫히면 <see cref="ShowAsync" /> 가 결과를 준다.
/// </summary>
internal sealed class SshKeySetupWindow : UserControl
{
    private sealed record KeyTypeOption(SshKeyType Type, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly TaskCompletionSource<SshKeySetupResult> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancel = new();
    private readonly bool _certificateMode;
    private readonly TabControl _tabs = new();
    private readonly AutoCompleteBox _existingKey = new() { FilterMode = AutoCompleteFilterMode.None, MinimumPrefixLength = 0 };
    private readonly TextBox _publicKey = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 85, FontFamily = Mono };
    private readonly Button _copyPublicKey = new() { Content = null, IsEnabled = false };
    private readonly Button _savePublicKey = new() { IsEnabled = false };
    private readonly TextBlock _registrationDescription = new() { TextWrapping = TextWrapping.Wrap, Classes = { "subtle" } };
    private readonly StackPanel _registrationCommands = new();
    private readonly TextBox _commandBox = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 205, FontFamily = Mono };
    private readonly Button _copyCommand = new() { IsEnabled = false };
    private readonly TextBlock _certificateDescription = new() { TextWrapping = TextWrapping.Wrap, Classes = { "subtle" }, IsVisible = false };
    private readonly ComboBox _keyType = new() { MinWidth = 200 };
    private readonly AutoCompleteBox _newKeyPath = new() { FilterMode = AutoCompleteFilterMode.None, MinimumPrefixLength = 0 };
    private readonly StackPanel _passphrasePanel = new() { Spacing = 4 };
    private readonly TextBlock _ed25519Hint = new() { TextWrapping = TextWrapping.Wrap, Classes = { "subtle" }, IsVisible = false, Text = Loc.T("SshSetup_Ed25519Hint") };
    private readonly TextBox _newPassphrase = new() { PasswordChar = '•' };
    private readonly TextBox _confirmPassphrase = new() { PasswordChar = '•' };
    private readonly TextBox _comment = new();
    private readonly TextBlock _status = new() { Classes = { "DialogStatus" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _generate;
    private string _pasteCommand = string.Empty;
    private string? _loadedKeyPath;
    private string? _passphrase;
    private string? _suggestedPath;
    private bool _busy;
    private bool _closed;
    private string? _generatedPath;
    private string? _generatedPassphrase;

    private static readonly FontFamily Mono = ProxmoxClient.UI.AppFonts.MonoText;

    private SshKeySetupWindow(string privateKeyPath, string? passphrase, string userName, bool certificateMode,
        bool defaultKeysMode)
    {
        _certificateMode = certificateMode;
        _passphrase = passphrase;
        _generate = new Button { Content = Loc.T("SshSetup_Generate"), Classes = { "accent" } };

        _keyType.ItemsSource = new[]
        {
            new KeyTypeOption(SshKeyType.Rsa3072, Loc.T("SshSetup_Rsa3072")),
            new KeyTypeOption(SshKeyType.Ed25519, Loc.T("SshSetup_Ed25519"))
        };
        _keyType.SelectedIndex = 0;
        _comment.Text = userName.Length > 0 ? $"{userName}@ProxmoxClient" : "ProxmoxClient";
        var paths = !defaultKeysMode && !string.IsNullOrWhiteSpace(privateKeyPath)
            ? new List<string> { privateKeyPath } : SshKeySetup.DiscoverDefaultKeys().ToList();
        _existingKey.ItemsSource = paths;
        _existingKey.Text = !defaultKeysMode && privateKeyPath.Length > 0 ? privateKeyPath : paths.FirstOrDefault() ?? string.Empty;
        _registrationDescription.Text = Loc.T(certificateMode ? "SshSetup_CertificateRegistration" : "SshSetup_RegistrationDescription",
            userName.Length > 0 ? userName : Loc.T("SshSetup_TargetUser"));
        _registrationCommands.IsVisible = !certificateMode;
        _certificateDescription.IsVisible = certificateMode;
        _certificateDescription.Text = Loc.T("SshSetup_CertificateNote");

        _existingKey.TextChanged += (_, _) => InvalidatePublicKey();
        Content = BuildLayout();
        _tabs.SelectedIndex = _existingKey.Text.Length == 0 ? 1 : 0;

        DetachedFromVisualTree += (_, _) =>
        {
            _closed = true;
            _done.TrySetResult(CurrentResult());
            _cancel.Cancel();
            _passphrase = null;
            _newPassphrase.Text = string.Empty;
            _confirmPassphrase.Text = string.Empty;
        };
    }

    /// <summary>키 설정 페이지를 열고 닫힐 때까지 기다린다.</summary>
    public static async Task<SshKeySetupResult> ShowAsync(string privateKeyPath, string? passphrase, string userName,
        bool certificateMode, bool defaultKeysMode)
    {
        var view = new SshKeySetupWindow(privateKeyPath, passphrase, userName, certificateMode, defaultKeysMode);
        await AppServices.Navigator.PushViewAsync(Loc.T("SshSetup_Title"), view);
        return await view._done.Task;
    }

    /// <summary>검증 도구용 — 설정 화면 내용만 만든다(닫기·결과 없이).</summary>
    internal static Control Preview() => new SshKeySetupWindow(string.Empty, null, "root", false, true);

    private SshKeySetupResult CurrentResult() => new(_generatedPath, _generatedPassphrase);

    /// <summary>닫기 — 페이지를 빼고 결과를 돌려준다(뒤로 가기로 빠진 경우에는 DetachedFromVisualTree 가 처리).</summary>
    private void Close()
    {
        if (_done.TrySetResult(CurrentResult())) _ = AppServices.Navigator.PopAsync();
    }

    private Control BuildLayout()
    {
        _tabs.Items.Add(new TabItem { Header = Loc.T("SshSetup_RegistrationTab"), Content = RegistrationTab() });
        _tabs.Items.Add(new TabItem { Header = Loc.T("SshSetup_GenerationTab"), Content = GenerationTab() });

        _status.Text = string.Empty;
        var close = new Button { Content = Loc.T("Sftp_Close"), Classes = { "DialogButton" }, MinWidth = 88, MinHeight = 40 };
        close.Click += (_, _) => Close();
        var footer = new DockPanel
        {
            Margin = new Thickness(20, 10),
            Children = { close, _status }
        };
        DockPanel.SetDock(close, Dock.Right);
        var border = new Border { Classes = { "DialogFooter" }, Child = footer };

        var grid = new Grid { RowDefinitions = RowDefinitions.Parse("*,Auto") };
        grid.Children.Add(new Border { Margin = new Thickness(16), Child = _tabs });
        Grid.SetRow(border, 1);
        grid.Children.Add(border);
        return grid;
    }

    private Control RegistrationTab()
    {
        _copyPublicKey.Content = Loc.T("SshSetup_CopyPublicKey");
        IconAssist.SetIcon(_copyPublicKey, StorageFiles.Icon("IconCopy"));
        _copyPublicKey.Click += async (_, _) => await CopyAsync(_publicKey.Text);
        _savePublicKey.Content = Loc.T("SshSetup_SavePublicKey");
        _savePublicKey.Click += async (_, _) => await SavePublicKeyAsync();
        _copyCommand.Content = Loc.T("SshSetup_CopyCommand");
        IconAssist.SetIcon(_copyCommand, StorageFiles.Icon("IconCopy"));
        _copyCommand.Click += async (_, _) =>
        {
            if (_pasteCommand.Length > 0) await CopyAsync(_pasteCommand);
        };
        var readKey = new Button { Content = Loc.T("SshSetup_ReadPublicKey"), HorizontalAlignment = HorizontalAlignment.Left };
        readKey.Click += async (_, _) => await ReadPublicKeyAsync();
        var docs = new Button { Content = Loc.T("SshSetup_OpenDocs"), HorizontalAlignment = HorizontalAlignment.Left };
        docs.Click += async (_, _) => await OpenDocsAsync();

        _registrationCommands.Children.Add(_commandBox);
        _registrationCommands.Children.Add(new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 12),
            Children = { _copyCommand }
        });
        _registrationCommands.Children.Add(Subtle(Loc.T("SshSetup_PasteCommandHint"), 0, 0, 8));
        _registrationCommands.Children.Add(Subtle(Loc.T("SshSetup_RegistrationNote")));

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 6 };
        panel.Children.Add(Label(Loc.T("SshSetup_PrivateKey")));
        var browseKey = new Button { Content = Loc.T("Sftp_Browse") };
        browseKey.Click += async (_, _) => await BrowseKeyAsync();
        panel.Children.Add(Row(browseKey, _existingKey));
        panel.Children.Add(Subtle(Loc.T("SshSetup_ExistingKeyHint"), 0, 0, 10));
        panel.Children.Add(readKey);
        panel.Children.Add(Label(Loc.T("SshSetup_PublicKey")));
        panel.Children.Add(_publicKey);
        panel.Children.Add(new WrapPanel
        {
            Margin = new Thickness(0, 8, 0, 12),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _copyPublicKey, _savePublicKey } }
            }
        });
        panel.Children.Add(_registrationDescription);
        panel.Children.Add(_registrationCommands);
        panel.Children.Add(_certificateDescription);
        panel.Children.Add(docs);
        return new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = panel };
    }

    private Control GenerationTab()
    {
        _keyType.SelectionChanged += (_, _) => OnKeyTypeChanged();
        _newKeyPath.Text = string.Empty;
        _newKeyPath.FilterMode = AutoCompleteFilterMode.None;
        _newKeyPath.MinimumPrefixLength = 0;
        var pathRow = PlatformInfo.IsDesktop
            ? (Control)Row(BrowseNewPathButton(), _newKeyPath)
            : _newKeyPath;
        _passphrasePanel.Children.Add(Label(Loc.T("SshSetup_NewPassphrase")));
        _passphrasePanel.Children.Add(_newPassphrase);
        _passphrasePanel.Children.Add(Label(Loc.T("SshSetup_ConfirmPassphrase")));
        _passphrasePanel.Children.Add(_confirmPassphrase);
        _passphrasePanel.Children.Add(Subtle(Loc.T("SshSetup_PassphraseHint"), 0, 0, 8));
        _generate.Click += async (_, _) => await GenerateAsync();

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 6 };
        panel.Children.Add(Subtle(Loc.T("SshSetup_GenerationHint"), 0, 0, 12));
        panel.Children.Add(Label(Loc.T("SshSetup_KeyType")));
        panel.Children.Add(_keyType);
        panel.Children.Add(Label(Loc.T("SshSetup_NewPath")));
        panel.Children.Add(pathRow);
        panel.Children.Add(_passphrasePanel);
        panel.Children.Add(_ed25519Hint);
        panel.Children.Add(Label(Loc.T("SshSetup_Comment")));
        panel.Children.Add(_comment);
        panel.Children.Add(new StackPanel { Margin = new Thickness(0, 12, 0, 0), Children = { _generate } });
        _generate.HorizontalAlignment = HorizontalAlignment.Left;
        OnKeyTypeChanged();
        return new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = panel };
    }

    private Button BrowseNewPathButton()
    {
        var button = new Button { Content = Loc.T("Sftp_Browse") };
        button.Click += async (_, _) => await ChooseNewPathAsync();
        return button;
    }

    private static TextBlock Label(string text) => new() { Text = text, Classes = { "label" } };

    private static TextBlock Subtle(string text, double left = 0, double top = 0, double bottom = 0) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "subtle" }, Margin = new Thickness(left, top, 0, bottom)
    };

    private static DockPanel Row(Button button, Control input)
    {
        button.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(button, Dock.Right);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(button);
        row.Children.Add(input);
        return row;
    }

    private void OnKeyTypeChanged()
    {
        if (_keyType.SelectedItem is not KeyTypeOption option) return;

        var supportsPhrase = option.Type == SshKeyType.Rsa3072;
        _passphrasePanel.IsVisible = supportsPhrase;
        _ed25519Hint.IsVisible = !supportsPhrase;
        if (!supportsPhrase)
        {
            _newPassphrase.Text = string.Empty;
            _confirmPassphrase.Text = string.Empty;
        }

        if (_newKeyPath.Text is null or { Length: 0 } || _newKeyPath.Text == _suggestedPath)
        {
            _suggestedPath = SshKeySetup.DefaultPrivateKeyPath(option.Type);
            _newKeyPath.Text = _suggestedPath;
        }
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
        _tabs.IsEnabled = false;
        _status.Text = Loc.T("SshSetup_Working");
        try { await action(); }
        catch (OperationCanceledException) { if (!_closed) _status.Text = Loc.T("GuestFiles_Cancelled"); }
        catch (Exception ex) { if (!_closed) _status.Text = Loc.T(ex.Message); }
        finally
        {
            _tabs.IsEnabled = true;
            _busy = false;
        }
    }

    private async Task BrowseKeyAsync()
    {
        if (await StorageFiles.PickUploadAsync(this, Loc.T("Sftp_KeyFilter"), Loc.T("Sftp_PrivateKeyPath")) is not { } file)
            return;

        _existingKey.Text = await KeyFiles.LocalPathAsync(file);
    }

    private async Task ChooseNewPathAsync()
    {
        if (await StorageFiles.PickSaveAsync(this, Path.GetFileName(_newKeyPath.Text ?? string.Empty), zip: false)
            is { } file && file.TryGetLocalPath() is { } local)
            _newKeyPath.Text = local;
    }

    private async Task ReadPublicKeyAsync() => await RunAsync(async () =>
    {
        ClearPublicKey();
        if (string.IsNullOrWhiteSpace(_existingKey.Text)) throw new InvalidOperationException(Loc.T("Sftp_KeyPathRequired"));
        var path = LocalPath(_existingKey.Text);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var key = await Task.Run(() => SshKeySetup.ReadPublicKey(path, _passphrase), _cancel.Token);
                _cancel.Token.ThrowIfCancellationRequested();
                var ready = SetPublicKey(path, key);
                _status.Text = Loc.T(!ready ? "Sftp_RegistrationCommandLong"
                    : _certificateMode ? "SshSetup_CertificatePublicKeyReady" : "SshSetup_PublicKeyReady");
                return;
            }
            catch (GuestFileException ex) when ((ex is SftpPassphraseRequiredException or SftpPassphraseException) && attempt < 2)
            {
                _cancel.Token.ThrowIfCancellationRequested();
                var result = await FormDialog.ShowAsync(Loc.T("SshSetup_UnlockKey"),
                [new() { Key = "passphrase", LabelKey = "Sftp_Passphrase", Kind = FormFieldKind.Password, Hint = ex.Message }]);
                if (result is null) throw new OperationCanceledException();
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
        _publicKey.Text = publicKey;
        _commandBox.Text = command;
        _copyPublicKey.IsEnabled = _savePublicKey.IsEnabled = true;
        _copyCommand.IsEnabled = pasteCommand.Length > 0;
        return _certificateMode || pasteCommand.Length > 0;
    }

    private void ClearPublicKey()
    {
        _loadedKeyPath = null;
        _pasteCommand = string.Empty;
        _publicKey.Text = string.Empty;
        _commandBox.Text = string.Empty;
        _copyPublicKey.IsEnabled = _savePublicKey.IsEnabled = _copyCommand.IsEnabled = false;
    }

    private void InvalidatePublicKey()
    {
        if (_loadedKeyPath is null) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(_existingKey.Text)
                && string.Equals(LocalPath(_existingKey.Text), _loadedKeyPath, StringComparison.OrdinalIgnoreCase)) return;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        ClearPublicKey();
        if (!_busy) _status.Text = string.Empty;
    }

    private static async Task CopyAsync(string? value)
    {
        await ClipboardHelper.SetTextAsync(value ?? string.Empty);
    }

    private async Task SavePublicKeyAsync() => await RunAsync(async () =>
    {
        if (await StorageFiles.PickSaveAsync(this, (_loadedKeyPath is null ? "id_ed25519" : Path.GetFileName(_loadedKeyPath)) + ".pub",
                zip: false) is not { } file)
        {
            _status.Text = string.Empty;
            return;
        }

        await using var output = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(output, new UTF8Encoding(false));
        await writer.WriteAsync((_publicKey.Text + "\n").AsMemory(), _cancel.Token);
        await writer.FlushAsync(_cancel.Token);
        _status.Text = Loc.T("SshSetup_PublicKeySaved", file.Name);
    });

    private async Task GenerateAsync() => await RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(_newKeyPath.Text)) throw new InvalidOperationException(Loc.T("Sftp_KeyPathRequired"));
        if (_newPassphrase.Text != _confirmPassphrase.Text)
            throw new InvalidOperationException(Loc.T("SshSetup_PassphraseMismatch"));
        if ((_newPassphrase.Text ?? string.Empty).Length > 1280) throw new InvalidOperationException(Loc.T("Sftp_PassphraseLong"));
        var type = _keyType.SelectedItem is KeyTypeOption choice ? choice.Type : SshKeyType.Rsa3072;
        var phrase = _newPassphrase.Text ?? string.Empty;
        var key = await SshKeySetup.GenerateAsync(LocalPath(_newKeyPath.Text), type, phrase, _comment.Text, _cancel.Token);
        _cancel.Token.ThrowIfCancellationRequested();
        _generatedPath = key.PrivateKeyPath;
        _generatedPassphrase = phrase.Length > 0 ? phrase : null;
        _passphrase = _generatedPassphrase;
        _existingKey.Text = key.PrivateKeyPath;
        SetPublicKey(key.PrivateKeyPath, key.PublicKey);
        _newPassphrase.Text = string.Empty;
        _confirmPassphrase.Text = string.Empty;
        _tabs.SelectedIndex = 0;
        _status.Text = Loc.T("SshSetup_Generated", key.PrivateKeyPath);
    });

    private async Task OpenDocsAsync()
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                await launcher.LaunchUriAsync(new Uri("https://man.openbsd.org/sshd.8#AUTHORIZED_KEYS_FILE_FORMAT"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            _status.Text = ex.Message;
        }
    }
}

/// <summary>선택한 키 파일의 로컬 경로 — 데스크톱은 그대로, 안드로이드 콘텐츠 URI 는 앱 폴더에 복사해 둔다(접속할 때 읽는다).</summary>
internal static class KeyFiles
{
    public static async Task<string> LocalPathAsync(IStorageFile file)
    {
        if (file.TryGetLocalPath() is { } local) return local;

        var folder = Path.Combine(Path.GetDirectoryName(ConsoleSettingsStore.DefaultPath)!, "keys");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, StorageFiles.SafeFileName(file.Name));
        await using var source = await file.OpenReadAsync();
        await using var output = File.Create(target);
        await source.CopyToAsync(output);
        return target;
    }
}

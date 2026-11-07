using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Login;

/// <summary>
///     2단계 인증 입력 — 방식(보안 키·TOTP·복구 키·Yubico OTP)을 고르고 코드를 넣거나 보안 키로 확인해 로그인을 마친다.
///     틀리면 오류를 보이고 그대로 다시 입력받는다. 보안 키(WebAuthn)는 이 기기의 <see cref="IWebAuthnProvider" /> 가
///     있을 때만 고를 수 있고, 없으면 그 까닭을 안내한다. 옛 U2F 질문은 지원하지 않는다.
/// </summary>
public sealed class TfaDialog : UserControl, IDismissible
{
    private readonly ProxmoxApiClient _api;
    private readonly TfaChallenge _challenge;
    private readonly IWebAuthnProvider _webAuthn;
    private CancellationTokenSource? _keyWait;
    private TaskCompletionSource<string?>? _pinWait;
    private readonly TextBox _code = new() { MinHeight = 40 };
    private readonly TextBox _pin = new() { MinHeight = 40, PasswordChar = '•', IsVisible = false, MaxLength = 63 };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _ok = new() { Content = L.T("Common_Ok"), MinWidth = 88, MinHeight = 40 };
    private readonly Button _cancel = new() { Content = L.T("Common_Cancel"), MinWidth = 88, MinHeight = 40 };
    private TfaKind _kind;
    private bool _busy;

    private TfaDialog(ProxmoxApiClient api, TfaChallenge challenge, IWebAuthnProvider webAuthn)
    {
        _api = api;
        _challenge = challenge;
        _webAuthn = webAuthn;
        var kinds = challenge.AvailableKinds(webAuthn.IsAvailable);
        _kind = kinds[0];

        TextInputOptions.SetAutoCapitalization(_code, false);
        TextInputOptions.SetShowSuggestions(_code, false);
        _code.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _ = SubmitAsync();
        };
        _code.AttachedToVisualTree += (_, _) => _code.Focus();
        _pin.PlaceholderText = L.T("Tfa_PinWatermark");
        _pin.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SubmitPin();
        };

        _ok.Classes.Add("accent");
        _ok.Click += (_, _) =>
        {
            // 보안 키가 PIN 을 기다리면 PIN 을 넘긴다
            if (_pinWait is not null) SubmitPin();
            else _ = SubmitAsync();
        };
        _cancel.Click += (_, _) =>
        {
            // 보안 키를 기다리는 중이면 기다림만 멈춘다
            if (_keyWait is { } wait) wait.Cancel();
            else Dismiss();
        };

        var body = new StackPanel { Margin = new Thickness(20, 18, 20, 16), Spacing = 10 };
        body.Children.Add(new TextBlock { Text = L.T("Tfa_Title"), Classes = { "headline" } });
        body.Children.Add(new TextBlock
        {
            Text = L.T("Tfa_Prompt", challenge.UserName), TextWrapping = TextWrapping.Wrap
        });

        // 방식이 둘 이상이면 고르는 단추(가로로 감싸 좁은 화면에서도 보인다)
        if (kinds.Count > 1)
        {
            var group = "tfa-kind-" + GetHashCode();
            var row = new WrapPanel();
            foreach (var kind in kinds)
            {
                var choice = new RadioButton
                {
                    Content = KindLabel(kind), GroupName = group, IsChecked = kind == _kind,
                    Margin = new Thickness(0, 0, 12, 4), Tag = kind
                };
                choice.IsCheckedChanged += (_, _) =>
                {
                    if (choice.IsChecked == true) SelectKind((TfaKind)choice.Tag!);
                };
                row.Children.Add(choice);
            }

            body.Children.Add(row);
        }

        body.Children.Add(_code);
        body.Children.Add(_pin);
        body.Children.Add(_hint);
        if (SecurityKeyNote(challenge, webAuthn) is { } note)
            body.Children.Add(new TextBlock
            {
                Text = note, Classes = { "subtle" },
                TextWrapping = TextWrapping.Wrap, FontSize = 12
            });
        body.Children.Add(_error);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _cancel, _ok }
        });

        Content = new ScrollViewer { Content = body };
        SelectKind(_kind);
    }

    /// <summary>확인되어 로그인을 마쳤으면 true(이때 <see cref="ProxmoxApiClient" /> 는 보통 로그인과 같은 인증 상태).</summary>
    public bool Completed { get; private set; }

    /// <summary>현재 고른 방식(테스트·스냅샷용).</summary>
    public TfaKind Kind => _kind;

    /// <summary>오류 줄 내용(테스트·스냅샷용).</summary>
    public string CurrentError => _error.Text ?? string.Empty;

    /// <summary>코드 칸의 화면 키보드 종류(테스트·스냅샷용).</summary>
    public TextInputContentType CodeContentType => TextInputOptions.GetContentType(_code);

    /// <summary>보안 키가 PIN 을 기다리고 있다(스냅샷 검사용).</summary>
    public bool WaitingForPin => _pinWait is not null;

    /// <summary>PIN 안내 줄(남은 횟수, 스냅샷 검사용).</summary>
    public string PinHint => _pinWait is null ? string.Empty : _hint.Text ?? string.Empty;

    /// <summary>PIN 을 넣고 확인한다(스냅샷 검사용).</summary>
    public void EnterPin(string pin)
    {
        _pin.Text = pin;
        SubmitPin();
    }

    /// <summary>
    ///     2단계 인증을 받아 로그인을 마친다. 취소하면 false. 앱에서 입력할 방식이 없으면(보안 키만) 안내하고 false.
    /// </summary>
    public static async Task<bool> RunAsync(ProxmoxApiClient api, TfaChallenge challenge)
    {
        var webAuthn = WebAuthnProviders.Current;
        if (challenge.AvailableKinds(webAuthn.IsAvailable).Count == 0)
        {
            await AppServices.Dialogs.AlertAsync(L.T("Tfa_Title"),
                challenge.OnlySecurityKey ? SecurityKeyNote(challenge, webAuthn)! : L.T("Tfa_NoMethod"));
            return false;
        }

        var dialog = new TfaDialog(api, challenge, webAuthn);
        await AppServices.Dialogs.ShowSheetAsync(dialog);
        return dialog.Completed;
    }

    /// <summary>코드를 넣고 확인한다(스냅샷 검사용 — 화면에서 누른 것과 같다).</summary>
    public Task SubmitAsync(string code)
    {
        _code.Text = code;
        return SubmitAsync();
    }

    /// <summary>보안 키로 확인한다(스냅샷 검사용 — "보안 키"를 고르고 확인을 누른 것과 같다).</summary>
    public Task UseSecurityKeyAsync(TfaKind kind = TfaKind.WebAuthn)
    {
        SelectKind(kind);
        return SubmitAsync();
    }

    /// <summary>방식을 바꾼다(스냅샷 검사용).</summary>
    public void SelectKind(TfaKind kind)
    {
        _kind = kind;
        _code.Text = string.Empty;
        _error.Text = string.Empty;
        var key = IsSecurityKey(kind);
        _code.IsVisible = !key;
        _ok.Content = key ? L.T("Tfa_WebAuthnStart") : L.T("Common_Ok");
        // TOTP 는 숫자 키보드, 나머지는 글자 키보드
        TextInputOptions.SetContentType(_code,
            kind == TfaKind.Totp ? TextInputContentType.Digits : TextInputContentType.Normal);
        _code.MaxLength = kind == TfaKind.Totp ? 8 : 64;
        _code.PlaceholderText = kind switch
        {
            TfaKind.Totp => L.T("Tfa_TotpWatermark"),
            TfaKind.Recovery => "xxxx-xxxx-xxxx-xxxx",
            _ => L.T("Tfa_YubicoWatermark")
        };
        _hint.Text = kind switch
        {
            TfaKind.Recovery => RecoveryHint(),
            TfaKind.WebAuthn or TfaKind.U2f => L.T("Tfa_WebAuthnHint"),
            _ => string.Empty
        };
        _hint.IsVisible = _hint.Text.Length > 0;
        if (key) return;
        if (TopLevel.GetTopLevel(_code) is not null) _code.Focus();
    }

    public void Dismiss()
    {
        if (_busy) return;

        AppServices.Dialogs.CloseSheet();
    }

    private string RecoveryHint()
    {
        var ids = _challenge.RecoveryKeyIds;
        var text = L.T("Tfa_RecoveryLeft", string.Join(", ", ids));
        return ids.Count <= 3 ? text + "\n" + L.T("Tfa_RecoveryLow") : text;
    }

    /// <summary>
    ///     보안 키 안내 — 서버가 WebAuthn 을 물었는데 이 기기에서 못 쓰면 그 까닭, 옛 U2F 만 물었으면 미지원. 쓸 수 있으면 null.
    /// </summary>
    private static string? SecurityKeyNote(TfaChallenge challenge, IWebAuthnProvider webAuthn)
    {
        if (!challenge.HasWebAuthn && !challenge.HasU2f) return null;
        // 옛 형식(PVE 7.2 이전)의 U2F 는 질문 내용이 없어 받을 수 없다
        if (challenge.IsLegacy) return L.T("Tfa_SecurityKeyUnsupported");

        var usable = challenge.SecurityKeyJson(TfaKind.WebAuthn) is not null ||
                     challenge.SecurityKeyJson(TfaKind.U2f) is not null;
        if (!usable) return L.T("Tfa_SecurityKeyUnsupported");

        return webAuthn.IsAvailable ? null : webAuthn.UnavailableReason;
    }

    private static bool IsSecurityKey(TfaKind kind)
    {
        return kind is TfaKind.WebAuthn or TfaKind.U2f;
    }

    private static string KindLabel(TfaKind kind)
    {
        return kind switch
        {
            TfaKind.WebAuthn => L.T("Tfa_KindWebAuthn"),
            TfaKind.U2f => L.T("Tfa_KindU2f"),
            TfaKind.Totp => L.T("Tfa_KindTotp"),
            TfaKind.Recovery => L.T("Tfa_KindRecovery"),
            _ => L.T("Tfa_KindYubico")
        };
    }

    private async Task SubmitAsync()
    {
        if (_busy) return;

        if (IsSecurityKey(_kind))
        {
            await SubmitSecurityKeyAsync();
            return;
        }

        var code = ProxmoxApiClient.NormalizeTfaResponse(_kind, _code.Text);
        if (code.Length == 0)
        {
            _error.Text = L.T("Tfa_EmptyCode");
            return;
        }

        _busy = true;
        _ok.IsEnabled = _cancel.IsEnabled = _code.IsEnabled = false;
        _error.Text = L.T("Tfa_Verifying");
        try
        {
            await _api.CompleteTfaAsync(_challenge, _kind, code);
            Completed = true;
            _busy = false;
            AppServices.Dialogs.CloseSheet();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 틀린 코드(401)·연결 오류 — 그대로 다시 입력받는다
            _error.Text = Services.ErrorText.Describe(ex);
            _code.Text = string.Empty;
        }
        finally
        {
            _busy = false;
            _ok.IsEnabled = _cancel.IsEnabled = _code.IsEnabled = true;
            if (!Completed) _code.Focus();
        }
    }

    /// <summary>
    ///     보안 키 서명을 받아 password=webauthn:{JSON}(옛 U2F 는 u2f:{JSON})으로 보낸다. 기다리는 동안 취소 단추는 기다림을 멈춘다.
    ///     키가 PIN 을 요구하면 이 창에 PIN 칸을 보여 받는다(<see cref="PromptPinAsync" />).
    /// </summary>
    private async Task SubmitSecurityKeyAsync()
    {
        _busy = true;
        _ok.IsEnabled = false;
        _error.Text = _webAuthn.PromptText;
        using var wait = new CancellationTokenSource();
        _keyWait = wait;
        try
        {
            var response = await _webAuthn.GetAssertionAsync(_api.ServerOrigin, _challenge.SecurityKeyJson(_kind)!,
                PromptPinAsync, wait.Token);
            _keyWait = null;
            if (response is null)
            {
                _error.Text = L.T("Tfa_WebAuthnCancelled");
                return;
            }

            _cancel.IsEnabled = false;
            _error.Text = L.T("Tfa_Verifying");
            await _api.CompleteSecurityKeyAsync(_challenge, _kind, response);
            Completed = true;
            _busy = false;
            AppServices.Dialogs.CloseSheet();
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            _error.Text = L.T("Tfa_WebAuthnCancelled");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 키 오류는 문구 그대로, 서버 거부(401)·연결 오류는 보통 설명으로
            _error.Text = wait.IsCancellationRequested ? L.T("Tfa_WebAuthnCancelled") :
                ex is WebAuthnException ? ex.Message : Services.ErrorText.Describe(ex);
        }
        finally
        {
            _keyWait = null;
            HidePin();
            _busy = false;
            _ok.IsEnabled = _cancel.IsEnabled = true;
        }
    }

    /// <summary>
    ///     보안 키 PIN 을 묻는다(보안 키 구현이 아무 스레드에서나 부른다) — PIN 칸·남은 횟수·바로 전 오류를 보이고,
    ///     확인을 누르면 PIN 을, 취소하면 기다림 취소로 끝낸다.
    /// </summary>
    private Task<string?> PromptPinAsync(PinPromptInfo info, CancellationToken ct)
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            var wait = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => wait.TrySetCanceled(ct));
            _pinWait = wait;
            _pin.Text = string.Empty;
            _pin.IsVisible = true;
            _hint.Text = info.RetriesLeft is { } left ? L.T("Tfa_PinPromptRetries", left) : L.T("Tfa_PinPrompt");
            _hint.IsVisible = true;
            _error.Text = info.Error ?? string.Empty;
            _ok.Content = L.T("Common_Ok");
            _ok.IsEnabled = true;
            if (TopLevel.GetTopLevel(_pin) is not null) _pin.Focus();
            return wait.Task;
        });
    }

    private void SubmitPin()
    {
        if (_pinWait is not { } wait) return;

        var pin = _pin.Text ?? string.Empty;
        if (pin.Length == 0)
        {
            _error.Text = L.T("Tfa_PinEmpty");
            return;
        }

        HidePin();
        _ok.IsEnabled = false;
        _error.Text = _webAuthn.PromptText;
        wait.TrySetResult(pin);
    }

    private void HidePin()
    {
        _pinWait = null;
        _pin.Text = string.Empty;
        _pin.IsVisible = false;
        _ok.Content = L.T("Tfa_WebAuthnStart");
        _hint.Text = L.T("Tfa_WebAuthnHint");
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Login;

/// <summary>
///     2단계 인증 입력 — 방식(TOTP 기본·복구 키·Yubico OTP)을 고르고 코드를 넣어 로그인을 마친다.
///     틀리면 오류를 보이고 그대로 다시 입력받는다. 보안 키(WebAuthn·U2F)는 기기 API 가 필요해 안내만 한다.
/// </summary>
public sealed class TfaDialog : UserControl, IDismissible
{
    private readonly ProxmoxApiClient _api;
    private readonly TfaChallenge _challenge;
    private readonly TextBox _code = new() { MinHeight = 40 };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _ok = new() { Content = L.T("Common_Ok"), MinWidth = 88, MinHeight = 40 };
    private readonly Button _cancel = new() { Content = L.T("Common_Cancel"), MinWidth = 88, MinHeight = 40 };
    private TfaKind _kind;
    private bool _busy;

    private TfaDialog(ProxmoxApiClient api, TfaChallenge challenge)
    {
        _api = api;
        _challenge = challenge;
        var kinds = challenge.SupportedKinds;
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

        _ok.Classes.Add("accent");
        _ok.Click += (_, _) => _ = SubmitAsync();
        _cancel.Click += (_, _) => Dismiss();

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
        body.Children.Add(_hint);
        if (challenge.HasWebAuthn || challenge.HasU2f)
            body.Children.Add(new TextBlock
            {
                Text = L.T("Tfa_SecurityKeyUnsupported"), Classes = { "subtle" },
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

    /// <summary>
    ///     2단계 인증을 받아 로그인을 마친다. 취소하면 false. 앱에서 입력할 방식이 없으면(보안 키만) 안내하고 false.
    /// </summary>
    public static async Task<bool> RunAsync(ProxmoxApiClient api, TfaChallenge challenge)
    {
        if (challenge.SupportedKinds.Count == 0)
        {
            await AppServices.Dialogs.AlertAsync(L.T("Tfa_Title"),
                challenge.OnlySecurityKey ? L.T("Tfa_SecurityKeyUnsupported") : L.T("Tfa_NoMethod"));
            return false;
        }

        var dialog = new TfaDialog(api, challenge);
        await AppServices.Dialogs.ShowSheetAsync(dialog);
        return dialog.Completed;
    }

    /// <summary>코드를 넣고 확인한다(스냅샷 검사용 — 화면에서 누른 것과 같다).</summary>
    public Task SubmitAsync(string code)
    {
        _code.Text = code;
        return SubmitAsync();
    }

    /// <summary>방식을 바꾼다(스냅샷 검사용).</summary>
    public void SelectKind(TfaKind kind)
    {
        _kind = kind;
        _code.Text = string.Empty;
        _error.Text = string.Empty;
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
        _hint.Text = kind == TfaKind.Recovery ? RecoveryHint() : string.Empty;
        _hint.IsVisible = _hint.Text.Length > 0;
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

    private static string KindLabel(TfaKind kind)
    {
        return kind switch
        {
            TfaKind.Totp => L.T("Tfa_KindTotp"),
            TfaKind.Recovery => L.T("Tfa_KindRecovery"),
            _ => L.T("Tfa_KindYubico")
        };
    }

    private async Task SubmitAsync()
    {
        if (_busy) return;

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
}

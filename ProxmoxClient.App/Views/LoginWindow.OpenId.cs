using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.App.Views;

/// <summary>
///     OpenID 영역 로그인 — 비밀번호 대신 브라우저에서 IdP 로그인을 하고, 받은 티켓을 이번 연결의 "비밀번호"로 넘긴다
///     (메인 창은 아이디 + 티켓으로 다시 로그인·갱신한다 — 저장하지 않는다).
/// </summary>
public partial class LoginWindow
{
    /// <summary>브라우저 로그인 기다림 — 창을 닫으면 멈춘다(로컬 포트도 바로 닫힌다).</summary>
    private CancellationTokenSource? _openIdWait;

    private bool IsOpenIdRealm => RealmBox.SelectedItem is PveAuthDomain { Type: "openid" };

    /// <summary>OpenID 영역이면 비밀번호 칸을 숨기고 리디렉션 주소 안내를 보인다.</summary>
    private void UpdateOpenIdUi()
    {
        var openId = IsOpenIdRealm;
        PassLabel.Visibility = PassBox.Visibility = openId ? Visibility.Collapsed : Visibility.Visible;
        OpenIdHint.Visibility = openId ? Visibility.Visible : Visibility.Collapsed;
        OpenIdHint.Text = openId ? Loc.T("OpenId_Hint", OpenIdLogin.RedirectUrl) : string.Empty;
    }

    private async Task ConnectOpenIdAsync(ConnectionProfile stored)
    {
        if (stored.UseVpn)
        {
            SetStatus(Loc.T("OpenId_NoVpn"));
            return;
        }

        var realm = ((PveAuthDomain)RealmBox.SelectedItem).Realm;
        var runtime = stored.Clone();
        runtime.AuthMode = AuthMode.Password;
        runtime.ApiTokenId = null;
        runtime.ApiTokenSecret = null;

        _busy = true;
        BtnLogin.IsEnabled = false;
        SetStatus(Loc.T("OpenId_Waiting"));
        var client = new ProxmoxApiClient(runtime);
        _openIdWait = new CancellationTokenSource();
        var wait = _openIdWait.Token;
        Closing += CancelOpenIdWait;
        try
        {
            await CertificateTrust.RunAsync(
                () => OpenIdLogin.RunAsync(client, realm, wait),
                rejection => CertificateTrust.Confirm(this, rejection, runtime),
                () => TrustStoredCertificateAsync(stored, runtime));

            // 서버가 정한 아이디(user@realm)와 받은 티켓으로 메인 창이 다시 로그인한다
            runtime.Password = SecureStringHelper.FromString(client.AuthTicket);
            Activate();
            RememberIdentity(stored, runtime);
            ResultProfile = runtime;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            // 창을 닫아 멈춘 것 — 알릴 곳이 없다
        }
        catch (Exception ex)
        {
            Activate();
            SetStatus(Loc.T("LoginWindow_M10", ex.Message));
        }
        finally
        {
            Closing -= CancelOpenIdWait;
            _openIdWait.Dispose();
            _openIdWait = null;
            client.Dispose();
            _busy = false;
            BtnLogin.IsEnabled = true;
        }
    }

    private void CancelOpenIdWait(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _openIdWait?.Cancel();
    }
}

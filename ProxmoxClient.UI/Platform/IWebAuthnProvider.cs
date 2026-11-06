using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     보안 키(WebAuthn) 2단계 인증 — 이 기기의 보안 키에서 서명을 받는다.
///     구현은 <see cref="WebAuthnProviders.CreateDefault" /> 한 곳에서 고른다: Windows 는 webauthn.dll(Windows 보안 창),
///     Linux 는 USB 보안 키(/dev/hidraw, CTAPHID), Android 는 헤드 프로젝트가 NFC 구현을 넣는다. macOS·iOS 는 아직 없다.
/// </summary>
public interface IWebAuthnProvider
{
    /// <summary>이 기기에서 보안 키를 쓸 수 있다(2단계 인증 창에 "보안 키"를 보인다).</summary>
    bool IsAvailable { get; }

    /// <summary>쓸 수 없을 때 안내 문구.</summary>
    string UnavailableReason { get; }

    /// <summary>키를 기다리는 동안 보일 안내(“보안 키를 대세요” 등).</summary>
    string PromptText { get; }

    /// <summary>
    ///     서명을 받아 PVE 2단계 응답 JSON(password 의 "webauthn:" 뒤, <see cref="WebAuthnAssertion.ToPveResponse" />)을 돌려준다.
    ///     <paramref name="serverOrigin" /> 은 clientDataJSON 의 origin(https://호스트:8006),
    ///     <paramref name="publicKeyJson" /> 은 질문의 webauthn 항목({"publicKey":{…}}). 사용자가 취소하면 null,
    ///     실패하면 <see cref="WebAuthnException" />(문구를 그대로 보인다).
    /// </summary>
    Task<string?> GetAssertionAsync(Uri serverOrigin, string publicKeyJson, CancellationToken ct);
}

/// <summary>운영체제별 보안 키 구현 선택 — OS 분기는 여기서만 한다.</summary>
public static class WebAuthnProviders
{
    private static IWebAuthnProvider? _current;

    /// <summary>2단계 인증 창이 쓰는 구현 — 헤드 프로젝트(Android)·스냅샷 검사가 바꿔 넣는다.</summary>
    public static IWebAuthnProvider Current
    {
        get => _current ??= CreateDefault();
        set => _current = value;
    }

    public static IWebAuthnProvider CreateDefault()
    {
        if (OperatingSystem.IsWindows()) return new WindowsWebAuthnProvider();
        if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid()) return new LinuxHidWebAuthnProvider();
        if (OperatingSystem.IsIOS()) return new UnavailableWebAuthnProvider("Tfa_WebAuthnNoIos");

        return new UnavailableWebAuthnProvider("Tfa_WebAuthnNoMac");
    }
}

/// <summary>보안 키를 쓸 수 없는 플랫폼 — 안내만 한다.</summary>
public sealed class UnavailableWebAuthnProvider(string reasonKey) : IWebAuthnProvider
{
    public bool IsAvailable => false;
    public string UnavailableReason => L.T(reasonKey);
    public string PromptText => string.Empty;

    public Task<string?> GetAssertionAsync(Uri serverOrigin, string publicKeyJson, CancellationToken ct)
    {
        throw new WebAuthnException(UnavailableReason);
    }
}

/// <summary>Windows — webauthn.dll(Windows 10 1903+). 키 선택·PIN·터치 안내는 Windows 보안 창이 한다.</summary>
public sealed class WindowsWebAuthnProvider : IWebAuthnProvider
{
    public bool IsAvailable => OperatingSystem.IsWindows() && WindowsWebAuthn.IsAvailable;
    public string UnavailableReason => L.T("Tfa_WebAuthnNoWindows");
    public string PromptText => L.T("Tfa_WebAuthnPromptWindows");

    public Task<string?> GetAssertionAsync(Uri serverOrigin, string publicKeyJson, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new WebAuthnException(UnavailableReason);

        return WindowsWebAuthn.GetAssertionAsync(OwnerWindowHandle(), serverOrigin, publicKeyJson, ct);
    }

    /// <summary>Windows 보안 창의 부모 — 지금 활성 창(없으면 주 창). UI 스레드에서 부른다.</summary>
    private static nint OwnerWindowHandle()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return 0;

        var window = desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
        return window?.TryGetPlatformHandle()?.Handle ?? 0;
    }
}

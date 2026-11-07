using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Api;

/// <summary>
///     Windows WebAuthn API(webauthn.dll, Windows 10 1903+) — 보안 키·Windows Hello 로 서명받는다.
///     Windows 가 키 선택·PIN·터치 안내 창을 직접 띄우고, 앱은 RP ID 와 clientDataJSON 을 마음대로 줄 수 있어
///     PVE 주소를 origin 으로 둔 clientDataJSON 을 직접 만든다(브라우저가 하는 일). WPF 앱·데스크톱 헤드 공용.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsWebAuthn
{
    private const string Dll = "webauthn.dll";
    private const int NteUserCancelled = unchecked((int)0x80090036);
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    private static bool? _available;

    /// <summary>webauthn.dll 이 있고 API 를 부를 수 있다(Windows 10 1903 이상).</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available is { } known) return known;

            try
            {
                _available = NativeLibrary.TryLoad(Dll, out _) && WebAuthNGetApiVersionNumber() >= 1;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _available = false;
            }

            return _available.Value;
        }
    }

    /// <summary>
    ///     서명을 받아 PVE 2단계 응답 JSON("webauthn:" 뒤)을 돌려준다. 사용자가 취소하면 null.
    ///     <paramref name="owner" /> 는 Windows 안내 창의 부모 창(HWND).
    /// </summary>
    public static async Task<string?> GetAssertionAsync(nint owner, Uri origin, string webauthnJson,
        CancellationToken ct)
    {
        var request = WebAuthnRequest.Parse(webauthnJson);
        var clientData = request.BuildClientDataJson(origin);
        var rpId = request.EffectiveRpId(origin);

        var cancelId = Guid.Empty;
        var hasCancelId = WebAuthNGetCancellationId(out cancelId) == 0;
        await using var reg = ct.Register(() =>
        {
            if (hasCancelId) _ = WebAuthNCancelCurrentOperation(in cancelId);
        });

        var assertion = await Task.Run(() => GetAssertion(owner, rpId, request, clientData, hasCancelId, cancelId), CancellationToken.None)
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return assertion?.ToPveResponse(request.Challenge);
    }

    private static unsafe WebAuthnAssertion? GetAssertion(nint owner, string rpId, WebAuthnRequest request,
        byte[] clientData, bool hasCancelId, Guid cancelId)
    {
        var handles = new List<GCHandle>();
        try
        {
            nint Pin(object o)
            {
                var h = GCHandle.Alloc(o, GCHandleType.Pinned);
                handles.Add(h);
                return h.AddrOfPinnedObject();
            }

            var credType = Pin("public-key\0".ToCharArray());
            var creds = new WebAuthnCredential[request.AllowCredentials.Count];
            for (var i = 0; i < creds.Length; i++)
            {
                var id = request.AllowCredentials[i];
                creds[i] = new WebAuthnCredential
                {
                    Version = 1, IdLength = (uint)id.Length, Id = Pin(id), CredentialType = credType
                };
            }

            var options = new GetAssertionOptions
            {
                Version = 3,
                TimeoutMilliseconds = (uint)request.TimeoutMs,
                CredentialList = new WebAuthnCredentials
                {
                    Count = (uint)creds.Length, Credentials = creds.Length > 0 ? Pin(creds) : 0
                },
                AuthenticatorAttachment = 0, // 아무 키
                UserVerificationRequirement = request.UserVerification switch
                {
                    "required" => 1u,
                    "discouraged" => 3u,
                    _ => 2u
                }
            };
            var usedAppId = 0;
            if (request.AppId is { } appId)
            {
                options.U2fAppId = Pin((appId + "\0").ToCharArray());
                options.U2fAppIdUsed = (nint)(&usedAppId);
            }

            var cancel = cancelId;
            if (hasCancelId) options.CancellationId = (nint)(&cancel);

            var client = new WebAuthnClientData
            {
                Version = 1,
                ClientDataJsonLength = (uint)clientData.Length,
                ClientDataJson = Pin(clientData),
                HashAlgId = Pin("SHA-256\0".ToCharArray())
            };

            var hr = WebAuthNAuthenticatorGetAssertion(owner, rpId, in client, in options, out var result);
            if (hr is NteUserCancelled or ErrorCancelled) return null;
            if (hr != 0 || result == 0)
                throw new WebAuthnException(Res.T("WebAuthn_DeviceError", ErrorName(hr)));

            try
            {
                var a = (WebAuthnAssertionNative*)result;
                return new WebAuthnAssertion(
                    Copy(a->Credential.Id, a->Credential.IdLength),
                    Copy(a->AuthenticatorData, a->AuthenticatorDataLength),
                    clientData,
                    Copy(a->Signature, a->SignatureLength),
                    a->UserIdLength > 0 ? Copy(a->UserId, a->UserIdLength) : null);
            }
            finally
            {
                WebAuthNFreeAssertion(result);
            }
        }
        finally
        {
            foreach (var h in handles) h.Free();
        }
    }

    private static byte[] Copy(nint p, uint length)
    {
        var bytes = new byte[length];
        if (length > 0) Marshal.Copy(p, bytes, 0, (int)length);
        return bytes;
    }

    private static string ErrorName(int hr)
    {
        try
        {
            return $"{Marshal.PtrToStringUni(WebAuthNGetErrorName(hr))} (0x{hr:X8})";
        }
        catch
        {
            return $"0x{hr:X8}";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnClientData
    {
        public uint Version;
        public uint ClientDataJsonLength;
        public nint ClientDataJson;
        public nint HashAlgId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnCredential
    {
        public uint Version;
        public uint IdLength;
        public nint Id;
        public nint CredentialType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnCredentials
    {
        public uint Count;
        public nint Credentials;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnExtensions
    {
        public uint Count;
        public nint Extensions;
    }

    /// <summary>WEBAUTHN_AUTHENTICATOR_GET_ASSERTION_OPTIONS 버전 3(appid · 취소 ID 까지).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GetAssertionOptions
    {
        public uint Version;
        public uint TimeoutMilliseconds;
        public WebAuthnCredentials CredentialList;
        public WebAuthnExtensions Extensions;
        public uint AuthenticatorAttachment;
        public uint UserVerificationRequirement;
        public uint Flags;
        public nint U2fAppId;
        public nint U2fAppIdUsed;
        public nint CancellationId;
    }

    /// <summary>WEBAUTHN_ASSERTION 버전 1 부분(뒤 필드는 읽지 않는다).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WebAuthnAssertionNative
    {
        public uint Version;
        public uint AuthenticatorDataLength;
        public nint AuthenticatorData;
        public uint SignatureLength;
        public nint Signature;
        public WebAuthnCredential Credential;
        public uint UserIdLength;
        public nint UserId;
    }

    [LibraryImport(Dll)]
    private static partial uint WebAuthNGetApiVersionNumber();

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WebAuthNAuthenticatorGetAssertion(nint hWnd, string rpId,
        in WebAuthnClientData clientData, in GetAssertionOptions options, out nint assertion);

    [LibraryImport(Dll)]
    private static partial void WebAuthNFreeAssertion(nint assertion);

    [LibraryImport(Dll)]
    private static partial int WebAuthNGetCancellationId(out Guid cancellationId);

    [LibraryImport(Dll)]
    private static partial int WebAuthNCancelCurrentOperation(in Guid cancellationId);

    [LibraryImport(Dll)]
    private static partial nint WebAuthNGetErrorName(int hr);
}

using System.Security.Cryptography.X509Certificates;
using Devolutions.IronRdp;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Rdp;

/// <summary>
///     RDCleanPath 로 RDP 연결을 세운다 — PVE rdpproxy 가 웹소켓 너머에서 X.224·TLS 를 대신 하고
///     서버 인증서를 돌려주면, 이쪽은 그 공개 키로 CredSSP(NTLM)를 마친 뒤 RDP 연결 단계를 끝까지 진행한다.
///     IronRDP 웹 클라이언트·.NET 예제(RDCleanPathConnection)와 같은 순서다. 이 패키지 버전은 그 도우미가 내부용이라
///     쿠키 인증·인증서 검증을 붙인 우리 웹소켓으로 같은 단계를 직접 밟는다.
/// </summary>
internal static class RdpHandshake
{
    /// <summary>연결을 끝까지 세우고 결과(채널 번호·화면 크기 등)를 돌려준다 — 이후 PDU 는 같은 채널로 오간다.</summary>
    public static async Task<ConnectionResult> ConnectAsync(RdpChannel channel, Config config, string destination,
        string token, CliprdrBackendFactory? clipboard)
    {
        // 클라이언트 주소는 클라이언트 정보 PDU 에만 들어간다 — 웹소켓 너머라 실제 주소는 의미가 없다
        var connector = ClientConnector.New(config, "127.0.0.1:0");
        connector.WithDynamicChannelDisplayControl(); // 창 크기에 맞춘 해상도 변경(디스플레이 제어 채널)
        if (clipboard is not null) connector.AttachStaticCliprdr(clipboard.BuildCliprdr()); // 클립보드 채널

        var serverPublicKey = await CleanPathAsync(channel, connector, destination, token).ConfigureAwait(false);
        connector.MarkSecurityUpgradeAsDone(); // TLS 는 프록시가 했다

        var writeBuf = WriteBuf.New();
        if (connector.ShouldPerformCredssp())
            await CredsspAsync(channel, connector, destination, serverPublicKey, writeBuf).ConfigureAwait(false);

        while (!connector.GetDynState().IsTerminal())
            await channel.StepAsync(connector, writeBuf).ConfigureAwait(false);

        var state = connector.ConsumeAndCastToClientConnectorState();
        if (state.GetEnumType() != ClientConnectorStateType.Connected)
            throw new IronRdpLibException(IronRdpLibExceptionType.ConnectionFailed,
                Res.T("Rdp_NotConnected", state.GetEnumType()));

        return state.GetConnectedResult();
    }

    /// <summary>RDCleanPath 요청(첫 X.224 PDU + 토큰)을 보내고 응답의 X.224 확인·서버 인증서를 받는다.</summary>
    private static async Task<byte[]> CleanPathAsync(
        RdpChannel channel, ClientConnector connector, string destination, string token)
    {
        var writeBuf = WriteBuf.New();
        var written = connector.StepNoInput(writeBuf);
        var x224Request = new byte[(int)written.GetSize().Get()];
        writeBuf.ReadIntoBuf(x224Request);

        var request = RDCleanPathPdu.NewRequest(x224Request, destination, token, string.Empty);
        await channel.WriteAsync(Utils.VecU8ToByte(request.ToDer())).ConfigureAwait(false);

        var answer = await channel.ReadByHintAsync(new RDCleanPathHint()).ConfigureAwait(false);
        var response = RDCleanPathPdu.FromDer(answer);
        switch (response.Type)
        {
            case RDCleanPathResultType.Response:
                break;
            case RDCleanPathResultType.GeneralError:
                throw new IronRdpLibException(IronRdpLibExceptionType.ConnectionFailed,
                    Res.T("Rdp_GatewayError", response.GetErrorCode(), response.GetErrorMessage()));
            default:
                throw new IronRdpLibException(IronRdpLibExceptionType.ConnectionFailed, Res.T("Rdp_Negotiation"));
        }

        writeBuf.Clear();
        connector.Step(Utils.VecU8ToByte(response.GetX224Response()), writeBuf);

        var chain = response.GetServerCertChain();
        if (chain.IsEmpty() || chain.Next() is not { } first)
            throw new IronRdpLibException(IronRdpLibExceptionType.ConnectionFailed, Res.T("Rdp_NoCertificate"));

        // CredSSP 는 TLS 공개 키에 묶인다 — 프록시가 받은 서버 인증서의 키를 쓴다(프록시는 신뢰한 PVE 서버 안에 있다)
        using var certificate = X509CertificateLoader.LoadCertificate(Utils.VecU8ToByte(first));
        return certificate.GetPublicKey();
    }

    /// <summary>CredSSP(NTLM) — 외부 인증 서버(Kerberos)가 필요한 구성은 지원하지 않는다.</summary>
    private static async Task CredsspAsync(RdpChannel channel, ClientConnector connector, string destination,
        byte[] serverPublicKey, WriteBuf writeBuf)
    {
        var init = CredsspSequence.Init(connector, destination, serverPublicKey, null);
        var sequence = init.GetCredsspSequence();
        var tsRequest = init.GetTsRequest();

        while (true)
        {
            var state = sequence.ProcessTsRequest(tsRequest).Start();
            if (!state.IsCompleted())
                throw new IronRdpLibException(IronRdpLibExceptionType.ConnectionFailed, Res.T("Rdp_NeedsNetworkAuth"));

            writeBuf.Clear();
            var written = sequence.HandleProcessResult(state.GetClientStateIfCompleted(), writeBuf);
            if (written.GetSize().IsSome())
            {
                var reply = new byte[(int)written.GetSize().Get()];
                writeBuf.ReadIntoBuf(reply);
                await channel.WriteAsync(reply).ConfigureAwait(false);
            }

            if (sequence.NextPduHint() is not { } hint) break;

            var pdu = await channel.ReadByHintAsync(hint).ConfigureAwait(false);
            if (sequence.DecodeServerMessage(pdu) is not { } next) break;

            tsRequest = next;
        }
    }
}

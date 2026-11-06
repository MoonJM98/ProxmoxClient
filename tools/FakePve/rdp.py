"""가짜 PVE RDP 콘솔 — rdpproxy 토큰 웹소켓(…/qemu/{vmid}/rdp/{token}) 위에서 RDCleanPath 프록시 노릇을 한다.
실제 RDP 서버(이 PC 의 xrdp 등, CONFIG['target'])에 TCP 로 붙어 X.224 연결 요청을 넘기고 확인을 받은 뒤 TLS 를 맺고,
RDCleanPath 응답(X.224 확인 + 서버 인증서 + 서버 주소)을 돌려준다. 그 뒤로는 웹소켓 ↔ TLS 바이트를 그대로 잇는다.
PDU 모양은 IronRDP ironrdp-rdcleanpath(RDCleanPathPdu, 모든 필드 EXPLICIT 문맥 태그)와 같다.
xrdp 와 이 앱의 IronRDP(2025.12.4) 사이에 안 맞는 세 가지는 프록시가 메운다(아래 DownFixer·UpFixer — 실제 PVE 프록시는
하지 않는다): 느린 경로 화면 갱신, 머리만 있는 Deactivate All, drdynvc 능력 버전.
로그: 'RDP ...' 줄(연결·요청·응답·유니코드 키·바이트 수·끊김)."""
import base64, hashlib, os, select, socket, ssl, struct, sys, threading

GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11'
VERSION_1 = 3390
DEBUG = os.environ.get('FAKEPVE_RDP_DEBUG') == '1'  # 오가는 덩어리마다 로그

# 대상 RDP 서버와 rdpproxy 가 돌려줄 일회성 계정 — POST /__fake/rdp 로 바꾼다(기본: 환경 변수)
CONFIG = {
    'target': os.environ.get('FAKEPVE_RDP_TARGET', '127.0.0.1:3389'),
    'user': os.environ.get('FAKEPVE_RDP_USER', 'rdptest'),
    'password': os.environ.get('FAKEPVE_RDP_PASSWORD', ''),
}
TOKENS = {}  # 토큰 → vmid (한 번 쓰면 지운다)
ACTIVE = []  # 지금 열린 웹소켓 소켓 — /__fake/rdp-drop 으로 끊는다
ACTIVE_LOCK = threading.Lock()


def log(text):
    sys.stderr.write('RDP %s\n' % text)
    sys.stderr.flush()


# ---- 아주 작은 DER ----
def der_len(n):
    if n < 0x80:
        return bytes([n])
    b = n.to_bytes((n.bit_length() + 7) // 8, 'big')
    return bytes([0x80 | len(b)]) + b


def tlv(tag, value):
    return bytes([tag]) + der_len(len(value)) + value


def der_int(n):
    b = n.to_bytes(max(1, (n.bit_length() + 8) // 8), 'big')  # 맨 앞 비트가 1 이면 0 을 덧붙인다(양수)
    return tlv(0x02, b)


def ctx(n, inner):
    return tlv(0xA0 | n, inner)  # 문맥 태그 [n] EXPLICIT(구조형)


def der_read(buf, pos):
    """(태그, 값, 다음 위치)."""
    tag = buf[pos]
    n = buf[pos + 1]
    pos += 2
    if n & 0x80:
        k = n & 0x7F
        n = int.from_bytes(buf[pos:pos + k], 'big')
        pos += k
    return tag, buf[pos:pos + n], pos + n


def der_total(buf):
    """버퍼 맨 앞 DER 값 전체 길이 — 아직 모자라면 None."""
    if len(buf) < 2:
        return None
    n = buf[1]
    head = 2
    if n & 0x80:
        k = n & 0x7F
        if len(buf) < 2 + k:
            return None
        n = int.from_bytes(buf[2:2 + k], 'big')
        head += k
    return head + n


def decode_pdu(buf):
    tag, body, _ = der_read(buf, 0)
    assert tag == 0x30, 'RDCleanPath: SEQUENCE 가 아님 %02x' % tag
    fields = {}
    pos = 0
    while pos < len(body):
        t, inner, pos = der_read(body, pos)
        it, iv, _ = der_read(inner, 0)
        n = t & 0x1F
        if it == 0x02:
            fields[n] = int.from_bytes(iv, 'big')
        elif it == 0x0C:
            fields[n] = iv.decode('utf-8')
        else:
            fields[n] = bytes(iv)
    return fields


def response_pdu(x224, chain, addr):
    certs = b''.join(tlv(0x04, c) for c in chain)
    body = ctx(0, der_int(VERSION_1)) + ctx(6, tlv(0x04, x224)) + ctx(7, tlv(0x30, certs)) \
        + ctx(9, tlv(0x0C, addr.encode()))
    return tlv(0x30, body)


def error_pdu(code, http_status=None):
    err = ctx(0, der_int(code))
    if http_status is not None:
        err += ctx(1, der_int(http_status))
    return tlv(0x30, ctx(0, der_int(VERSION_1)) + ctx(1, tlv(0x30, err)))


# ---- 웹소켓(서버 쪽) ----
class WebSocket:
    def __init__(self, sock):
        self.sock = sock
        self.buf = b''
        self.lock = threading.Lock()

    def frames(self):
        """버퍼에 다 들어온 프레임을 꺼낸다 — (opcode, payload) 목록."""
        out = []
        while True:
            b = self.buf
            if len(b) < 2:
                return out
            length = b[1] & 0x7F
            pos = 2
            if length == 126:
                if len(b) < 4:
                    return out
                length = struct.unpack('>H', b[2:4])[0]
                pos = 4
            elif length == 127:
                if len(b) < 10:
                    return out
                length = struct.unpack('>Q', b[2:10])[0]
                pos = 10
            masked = b[1] & 0x80
            if masked:
                pos += 4
            if len(b) < pos + length:
                return out
            payload = b[pos:pos + length]
            if masked:
                mask = b[pos - 4:pos]
                payload = bytes(p ^ mask[i % 4] for i, p in enumerate(payload)) if length < 64 else _unmask(payload, mask)
            out.append((b[0] & 0x0F, payload))
            self.buf = b[pos + length:]

    def fill(self):
        chunk = self.sock.recv(65536)
        if not chunk:
            raise EOFError
        self.buf += chunk

    def send(self, data, opcode=2):
        n = len(data)
        head = bytes([0x80 | opcode])
        if n < 126:
            head += bytes([n])
        elif n < 65536:
            head += bytes([126]) + struct.pack('>H', n)
        else:
            head += bytes([127]) + struct.pack('>Q', n)
        with self.lock:
            sendall(self.sock, head + data)


def sendall(sock, data):
    """차단·비차단 SSL 소켓 모두 — 비차단이면 쓸 수 있을 때까지 기다리며 끝까지 보낸다."""
    view = memoryview(data)
    while view:
        try:
            n = sock.send(view)
            view = view[n:]
        except (ssl.SSLWantWriteError, BlockingIOError):
            select.select([], [sock], [], 10)
        except ssl.SSLWantReadError:
            select.select([sock], [], [], 10)


def recv_nb(sock):
    """비차단 읽기 — 응용 데이터가 아직 없으면(TLS 1.3 세션 티켓 등 제어 레코드만 왔다) None, 닫혔으면 b''."""
    try:
        return sock.recv(65536)
    except (ssl.SSLWantReadError, ssl.SSLWantWriteError, BlockingIOError):
        return None


def _unmask(payload, mask):
    m = (mask * (len(payload) // 4 + 1))[:len(payload)]
    return (int.from_bytes(payload, 'big') ^ int.from_bytes(m, 'big')).to_bytes(len(payload), 'big')


def read_tpkt(sock):
    head = b''
    while len(head) < 4:
        c = sock.recv(4 - len(head))
        if not c:
            raise EOFError('RDP 서버가 X.224 확인 전에 닫음')
        head += c
    n = struct.unpack('>H', head[2:4])[0]
    body = b''
    while len(body) < n - 4:
        c = sock.recv(n - 4 - len(body))
        if not c:
            raise EOFError
        body += c
    return head + body


# ---- xrdp 호환(서버 → 클라이언트): 느린 경로(slow-path) 화면 갱신 → 빠른 경로, 머리만 있는 Deactivate All 채우기 ----
# xrdp 는 로그인·창 관리자 화면을 그릴 때 비트맵을 느린 경로 Update PDU(TPKT/MCS/공유 데이터 PDU)로 보낸다
# (libxrdp_send_bitmap 은 늘 느린 경로). 이 앱의 IronRDP(2025.12.4)는 활성 단계에서 느린 경로 Update PDU 를 받으면
# "unhandled PDU: Update PDU" 로 세션을 끝낸다. PVE 의 RDP 서버(qemu-rdp)와 Windows 는 빠른 경로로 보내므로
# 앱 문제가 아니다 — 가짜 프록시는 TLS 를 직접 풀고 있으니 같은 내용(TS_UPDATE_BITMAP/PALETTE/SYNCHRONIZE)을 빠른 경로
# 갱신으로 바꿔 넘긴다. 그 밖의 PDU 는 그대로.
FP_CODES = {1: 0x1, 2: 0x2, 3: 0x3}  # 느린 경로 updateType → 빠른 경로 updateCode(BITMAP·PALETTE·SYNCHRONIZE)
FP_MAX_FRAGMENT = 16000


def _fastpath(code, data):
    out = b''
    pieces = [data[i:i + FP_MAX_FRAGMENT] for i in range(0, len(data), FP_MAX_FRAGMENT)] or [b'']
    for i, piece in enumerate(pieces):
        frag = 0 if len(pieces) == 1 else (2 if i == 0 else (1 if i == len(pieces) - 1 else 3))
        update = bytes([code | (frag << 4)]) + struct.pack('<H', len(piece)) + piece
        n = len(update) + 3
        out += bytes([0x00, 0x80 | (n >> 8), n & 0xFF]) + update
    return out


def _with_payload(pdu, mcs_head_end, payload):
    """TPKT·X.224·MCS 머리(길이 칸 앞까지)는 두고 내용만 바꿔 길이를 다시 쓴다."""
    n = len(payload)
    per = bytes([n]) if n < 0x80 else bytes([0x80 | (n >> 8), n & 0xFF])
    body = pdu[4:mcs_head_end] + per + payload
    return b'\x03\x00' + struct.pack('>H', len(body) + 4) + body


def _slow_update(pdu):
    """느린 경로 Update PDU 면 빠른 경로로 바꾼 바이트, 머리만 있는 Deactivate All 이면 채운 것, 아니면 None."""
    try:
        if pdu[4:7] != b'\x02\xf0\x80' or pdu[7] != 0x68:  # X.224 데이터 + MCS SendDataIndication
            return None
        head_end = 8 + 4 + 1  # initiator·channelId·dataPriority/segmentation
        n = pdu[head_end]
        pos = head_end + (2 if n & 0x80 else 1)
        total, pdu_type = struct.unpack('<HH', pdu[pos:pos + 4])
        if pdu_type & 0x0F == 6 and total == 6:
            # xrdp 는 해상도를 바꿀 때 Deactivate All 을 머리(6바이트)만 보낸다 — IronRDP 2025.12.4 는 shareId·
            # sourceDescriptor 가 없으면 "decode error" 로 세션을 끝낸다(새 버전은 받는다). 빈 값으로 채운다
            payload = struct.pack('<HHH', 13, pdu_type, struct.unpack('<H', pdu[pos + 4:pos + 6])[0]) \
                + struct.pack('<IH', 0x000103ea, 1) + b'\x00'
            return _with_payload(pdu, head_end, payload)
        if pdu_type & 0x0F != 7:  # 데이터 PDU 만
            return None
        share = pos + 6
        pdu_type2, compressed = pdu[share + 8], pdu[share + 9]
        if pdu_type2 != 2 or compressed & 0x20:
            return None
        body = pdu[share + 12:]
        update_type = struct.unpack('<H', body[:2])[0]
        if update_type not in FP_CODES:
            return None
        if DEBUG and update_type == 1:
            count = struct.unpack('<H', body[2:4])[0]
            r = struct.unpack('<HHHHHHHHH', body[4:22])
            log('bitmap rects=%d first dest=(%d,%d)-(%d,%d) size=%dx%d bpp=%d flags=0x%x len=%d' % ((count,) + r))
        return _fastpath(FP_CODES[update_type], b'' if update_type == 3 else body)
    except (IndexError, struct.error):
        return None


class DownFixer:
    """서버 → 클라이언트 바이트를 PDU 단위로 잘라 느린 경로 갱신만 바꾼다."""

    def __init__(self):
        self.buf = b''
        self.converted = 0
        self.off = False

    def feed(self, data):
        if self.off:
            return data
        self.buf += data
        out = b''
        while len(self.buf) >= 4:
            b = self.buf
            if b[0] == 3:
                n = struct.unpack('>H', b[2:4])[0]
            elif b[0] & 0x3 == 0:  # 빠른 경로
                n = ((b[1] & 0x7F) << 8 | b[2]) if b[1] & 0x80 else b[1]
            else:
                self.off = True  # 모르는 형식(CredSSP 등) — 이 연결은 손대지 않고 그대로 흘린다
                out += b
                self.buf = b''
                break
            if n < 2 or len(b) < n:
                break
            pdu, self.buf = b[:n], b[n:]
            fixed = _slow_update(pdu) if pdu[0] == 3 else None
            if fixed is not None:
                self.converted += 1
            out += fixed if fixed is not None else pdu
        return out


# 빠른 경로 입력 이벤트 길이(eventCode → 머리 뒤 바이트 수): 스캔코드·마우스·마우스X·동기화·유니코드·상대 마우스·QoE
FP_INPUT_SIZES = {0: 1, 1: 6, 2: 6, 3: 0, 4: 2, 5: 4, 6: 4}


def log_fastpath_input(pdu):
    """빠른 경로 입력 중 유니코드 키를 'RDP unicode U+XXXX down|up' 로 남긴다(서버가 받아 쓰지 않아도 보낸 것을 확인)."""
    try:
        count = (pdu[0] >> 2) & 0x0F
        pos = 3 if pdu[1] & 0x80 else 2
        if count == 0:
            count, pos = pdu[pos], pos + 1
        for _ in range(count):
            head = pdu[pos]
            code, flags = head >> 5, head & 0x1F
            if code == 4:
                cp = struct.unpack('<H', pdu[pos + 1:pos + 3])[0]
                log('unicode U+%04X %s' % (cp, 'up' if flags & 1 else 'down'))
            pos += 1 + FP_INPUT_SIZES[code]
    except (IndexError, KeyError, struct.error):
        pass


class UpFixer(DownFixer):
    """클라이언트 → 서버: drdynvc 능력 응답 버전 1 → 2. xrdp 는 버전 1 을 거절해(\"Dynamic Virtual Channel version 1 is
    not supported\") 동적 채널(디스플레이 제어 = 창 크기에 맞춘 해상도)이 열리지 않는다. IronRDP 2025.12.4 는 서버가
    무엇을 묻든 1 로 답한다(새 버전은 2). 버전 2 는 1 에 우선순위 값만 더한 것이라 클라이언트 동작은 같다."""

    def feed(self, data):
        if self.off:
            return data
        self.buf += data
        out = b''
        while len(self.buf) >= 4:
            b = self.buf
            if b[0] == 3:
                n = struct.unpack('>H', b[2:4])[0]
            elif b[0] & 0x3 == 0:
                n = ((b[1] & 0x7F) << 8 | b[2]) if b[1] & 0x80 else b[1]
            else:
                self.off = True
                out += b
                self.buf = b''
                break
            if n < 2 or len(b) < n:
                break
            pdu, self.buf = b[:n], b[n:]
            if pdu[0] != 3:
                log_fastpath_input(pdu)
            # TPKT·X.224·MCS SendDataRequest(0x64) … CHANNEL_PDU_HEADER(8) + DYNVC_CAPS_RSP(50 00 01 00)
            if pdu[0] == 3 and pdu[7:8] == b'\x64' and pdu.endswith(b'\x04\x00\x00\x00\x03\x00\x00\x00\x50\x00\x01\x00'):
                pdu = pdu[:-2] + b'\x02\x00'
                self.converted += 1
            out += pdu
        return out


def serve(handler, vmid, token):
    key = handler.headers.get('Sec-WebSocket-Key', '')
    accept = base64.b64encode(hashlib.sha1((key + GUID).encode()).digest()).decode()
    handler.send_response(101)
    handler.send_header('Upgrade', 'websocket')
    handler.send_header('Connection', 'Upgrade')
    handler.send_header('Sec-WebSocket-Accept', accept)
    handler.end_headers()
    handler.wfile.flush()
    conn = handler.connection
    ws = WebSocket(conn)
    with ACTIVE_LOCK:
        ACTIVE.append(conn)
    log('connected vmid=%s' % vmid)
    server = None
    try:
        # 1) RDCleanPath 요청(웹소켓 프레임 여러 개로 나뉠 수 있다)
        data = b''
        while der_total(data) is None or len(data) < der_total(data):
            frames = ws.frames()
            if not frames:
                ws.fill()
                continue
            for op, payload in frames:
                if op == 8:
                    raise EOFError
                if op == 9:
                    ws.send(payload, 0xA)
                elif op in (0, 1, 2):
                    data += payload
        total = der_total(data)
        req, rest = decode_pdu(data[:total]), data[total:]
        log('request version=%s destination=%s token_ok=%s x224=%d pcb=%r' % (
            req.get(0), req.get(2), req.get(3) == token, len(req.get(6, b'')), req.get(5)))
        if req.get(0) != VERSION_1 or 6 not in req:
            ws.send(error_pdu(2))
            return
        if req.get(3) != token or TOKENS.pop(token, None) is None:  # rdpproxy 가 준 토큰, 한 번만
            log('bad proxy token')
            ws.send(error_pdu(1, 403))
            return
        # 2) 대상에 TCP → X.224 요청 넘기고 확인 받기 → TLS
        host, port = CONFIG['target'].rsplit(':', 1)
        try:
            raw = socket.create_connection((host, int(port)), timeout=10)
        except OSError as e:
            log('target unreachable %s: %s' % (CONFIG['target'], e))
            ws.send(error_pdu(103))
            return
        raw.sendall(req[6])
        x224 = read_tpkt(raw)
        log('x224 confirm %s' % x224.hex())
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        tls.check_hostname = False
        tls.verify_mode = ssl.CERT_NONE
        server = tls.wrap_socket(raw, server_hostname=None)
        server.settimeout(None)
        chain = [server.getpeercert(binary_form=True)]  # 서버 인증서(앱은 첫 인증서의 공개 키로 CredSSP 를 묶는다)
        log('tls %s certs=%d' % (server.version(), len(chain)))
        ws.send(response_pdu(x224, chain, CONFIG['target']))
        if rest:
            server.sendall(rest)
        # 3) 바이트 잇기 — 한 스레드에서 select(두 SSL 소켓을 여러 스레드에서 같이 쓰지 않는다). 비차단으로 읽는다:
        # 읽을 수 있다고 나와도 TLS 제어 레코드(세션 티켓)뿐이면 응용 데이터가 없어 차단 읽기는 멈춘다
        conn.setblocking(False)
        server.setblocking(False)
        up = down = 0
        fixer = DownFixer()
        up_fixer = UpFixer()
        while True:
            ready = [s for s in (conn, server) if s.pending()]
            if not ready:
                ready, _, _ = select.select([conn, server], [], [], 30)
            for s in ready:
                chunk = recv_nb(s)
                if chunk is None:
                    continue
                if not chunk:
                    raise EOFError('server closed' if s is server else 'client closed')
                if s is server:
                    down += len(chunk)
                    if DEBUG: log('down %d %s' % (len(chunk), chunk[:48].hex()))
                    chunk = fixer.feed(chunk)
                    if chunk:
                        ws.send(chunk)
                    continue
                ws.buf += chunk
                for op, payload in ws.frames():
                    if op == 8:
                        raise EOFError('client closed')
                    if op == 9:
                        ws.send(payload, 0xA)  # ping → pong(클라이언트 연결 유지 확인)
                    elif op in (0, 1, 2) and payload:
                        up += len(payload)
                        if DEBUG: log('up %d' % len(payload))
                        payload = up_fixer.feed(payload)
                        if payload:
                            sendall(server, payload)
    except (EOFError, ConnectionError, OSError, ssl.SSLError, AssertionError) as e:
        log('end %s: %s · up %d down %d slow-path→fast-path %d dvc-caps %d' % (
            type(e).__name__, e, locals().get('up', 0), locals().get('down', 0),
            locals()['fixer'].converted if 'fixer' in locals() else 0,
            locals()['up_fixer'].converted if 'up_fixer' in locals() else 0))
    finally:
        with ACTIVE_LOCK:
            if conn in ACTIVE:
                ACTIVE.remove(conn)
        if server is not None:
            try:
                server.close()
            except OSError:
                pass
        log('closed')


def drop_all():
    """열린 RDP 웹소켓을 모두 끊는다(다시 연결 검사) — 닫기 프레임 없이 TCP 를 끊는다."""
    with ACTIVE_LOCK:
        conns = list(ACTIVE)
    for c in conns:
        try:
            c.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
    log('dropped %d' % len(conns))
    return len(conns)


def new_token(vmid):
    token = 'PVERDP:%d:%s' % (vmid, hashlib.sha1(os.urandom(16)).hexdigest()[:16])
    TOKENS[token] = vmid
    return token

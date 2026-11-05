"""가짜 VNC 콘솔 — vncwebsocket 웹소켓 위에서 RFB 3.8(보안 None)로 800x600 화면을 보내고,
받은 포인터·키 이벤트를 'RFB ...' 줄로 로그에 남긴다(모바일 조작성 검사에서 제스처 → 이벤트를 확인)."""
import base64, hashlib, struct, sys, threading, time

W, H = 800, 600
GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11'


def log(text):
    sys.stderr.write('RFB %s\n' % text)
    sys.stderr.flush()


def make_desktop():
    # BGRX: 위쪽 작업 표시줄 + 창 두 개 + 격자(확대해서 보기 좋게)
    buf = bytearray(W * H * 4)
    for y in range(H):
        for x in range(W):
            i = (y * W + x) * 4
            if y < 32:
                b, g, r = 0x40, 0x30, 0x20
            elif 80 <= x < 460 and 70 <= y < 360:
                b, g, r = (0xCC, 0x7A, 0x00) if y < 100 else (0xF5, 0xF5, 0xF5)
            elif 360 <= x < 760 and 260 <= y < 560:
                b, g, r = (0x3C, 0x8C, 0x2E) if y < 290 else (0xFF, 0xFF, 0xFF)
            else:
                b, g, r = 0x80 + (x * 60 // W), 0x50 + (y * 60 // H), 0x30
                if x % 50 == 0 or y % 50 == 0:
                    b, g, r = b + 20, g + 20, r + 20
            buf[i:i + 4] = bytes((b & 255, g & 255, r & 255, 0))
    return bytes(buf)


DESKTOP = None

# 화살표 커서 12x19 — '#' 검정 테두리, '.' 흰 채움
ARROW = [
    '#', '##', '#.#', '#..#', '#...#', '#....#', '#.....#', '#......#', '#.......#', '#........#',
    '#.........#', '#..........#', '#......#####', '#...#..#', '#..##..#', '#.#  #..#', '##   #..#', '#     #..#', '      ###']
CW, CH = 12, 19


def cursor_shape():
    pixels = bytearray(CW * CH * 4)
    mask = bytearray(((CW + 7) // 8) * CH)
    for y, row in enumerate(ARROW):
        for x, c in enumerate(row[:CW]):
            if c in '#.':
                v = 0 if c == '#' else 255
                pixels[(y * CW + x) * 4:(y * CW + x) * 4 + 4] = bytes((v, v, v, 0))
                mask[y * ((CW + 7) // 8) + x // 8] |= 0x80 >> (x % 8)
    return bytes(pixels) + bytes(mask)


class WebSocket:
    def __init__(self, sock):
        self.sock = sock
        self.buf = b''
        self.lock = threading.Lock()

    def _recv_exact(self, n):
        while len(self.buf) < n:
            chunk = self.sock.recv(65536)
            if not chunk:
                raise EOFError
            self.buf += chunk
        data, self.buf = self.buf[:n], self.buf[n:]
        return data

    def read_frame(self):
        b1, b2 = self._recv_exact(2)
        opcode = b1 & 0x0F
        length = b2 & 0x7F
        if length == 126:
            length = struct.unpack('>H', self._recv_exact(2))[0]
        elif length == 127:
            length = struct.unpack('>Q', self._recv_exact(8))[0]
        mask = self._recv_exact(4) if b2 & 0x80 else None
        payload = bytearray(self._recv_exact(length))
        if mask:
            for i in range(length):
                payload[i] ^= mask[i % 4]
        return opcode, bytes(payload)

    def send(self, data):
        n = len(data)
        head = bytes([0x82])
        if n < 126:
            head += bytes([n])
        elif n < 65536:
            head += bytes([126]) + struct.pack('>H', n)
        else:
            head += bytes([127]) + struct.pack('>Q', n)
        with self.lock:
            self.sock.sendall(head + data)


class Stream:
    """웹소켓 프레임을 이어 붙인 바이트 흐름."""

    def __init__(self, ws):
        self.ws = ws
        self.data = b''

    def read(self, n):
        while len(self.data) < n:
            opcode, payload = self.ws.read_frame()
            if opcode == 8:
                raise EOFError
            if opcode in (1, 2, 0):
                self.data += payload
        out, self.data = self.data[:n], self.data[n:]
        return out


def serve(handler):
    global DESKTOP
    if DESKTOP is None:
        DESKTOP = make_desktop()
    key = handler.headers.get('Sec-WebSocket-Key', '')
    accept = base64.b64encode(hashlib.sha1((key + GUID).encode()).digest()).decode()
    handler.send_response(101)
    handler.send_header('Upgrade', 'websocket')
    handler.send_header('Connection', 'Upgrade')
    handler.send_header('Sec-WebSocket-Accept', accept)
    handler.send_header('Sec-WebSocket-Protocol', 'binary')
    handler.end_headers()
    handler.wfile.flush()
    ws = WebSocket(handler.connection)
    stream = Stream(ws)
    log('connected')
    try:
        ws.send(b'RFB 003.008\n')
        stream.read(12)
        ws.send(bytes([1, 1]))  # 보안 유형 1개: None
        stream.read(1)
        ws.send(struct.pack('>I', 0))  # SecurityResult OK
        stream.read(1)  # ClientInit
        name = b'fake-vm'
        pf = bytes([32, 24, 0, 1]) + struct.pack('>HHH', 255, 255, 255) + bytes([16, 8, 0, 0, 0, 0])
        ws.send(struct.pack('>HH', W, H) + pf + struct.pack('>I', len(name)) + name)
        pending_full = True
        while True:
            t = stream.read(1)[0]
            if t == 0:  # SetPixelFormat
                stream.read(19)
            elif t == 2:  # SetEncodings
                _, n = struct.unpack('>BH', stream.read(3))
                encs = struct.unpack('>%di' % n, stream.read(4 * n))
                if -239 in encs:  # 커서 모양(RichCursor) — QEMU 처럼 커서는 화면에 그리지 않고 모양만 보낸다
                    ws.send(struct.pack('>BBH', 0, 0, 1) + struct.pack('>HHHHi', 0, 0, CW, CH, -239) + cursor_shape())
                    log('cursor shape sent')
                if -258 in encs:  # QEMU 확장 키 이벤트를 안다고 알린다(의사 영역 -258)
                    ws.send(struct.pack('>BBH', 0, 0, 1) + struct.pack('>HHHHi', 0, 0, 0, 0, -258))
                    log('qemu-ext-key on')
            elif t == 3:  # FramebufferUpdateRequest
                inc, x, y, w, h = struct.unpack('>BHHHH', stream.read(9))
                if not inc or pending_full:
                    pending_full = False
                    ws.send(struct.pack('>BBH', 0, 0, 1) + struct.pack('>HHHHi', 0, 0, W, H, 0) + DESKTOP)
                else:
                    # 바뀐 것 없음 — 실제 QEMU 처럼 입력 읽기를 막지 않고 조금 뒤에 답한다(막으면 입력이 50ms 씩 몰려 도착)
                    threading.Timer(0.05, lambda: ws.send(struct.pack('>BBH', 0, 0, 0))).start()
            elif t == 4:  # KeyEvent
                down, _, keysym = struct.unpack('>BHI', stream.read(7))
                log('key down=%d keysym=0x%x' % (down, keysym))
            elif t == 5:  # PointerEvent
                mask, x, y = struct.unpack('>BHH', stream.read(5))
                log('pointer mask=%d x=%d y=%d t=%.4f' % (mask, x, y, time.monotonic()))
            elif t == 6:  # ClientCutText
                _, n = struct.unpack('>3sI', stream.read(7))
                log('cut %r' % stream.read(n))
            elif t == 255:  # QEMU 확장 키 이벤트
                sub, down, keysym, keycode = struct.unpack('>BHII', stream.read(11))
                log('qkey down=%d keysym=0x%x keycode=0x%x' % (down, keysym, keycode))
            else:
                log('unknown message %d' % t)
                break
    except (EOFError, ConnectionError, OSError):
        pass
    log('closed')

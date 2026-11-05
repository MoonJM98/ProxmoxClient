"""가짜 터미널(termproxy) — pve-xtermjs 와 같은 프로토콜로 작은 셸을 흉내 낸다.
인증 줄("user:ticket\\n") → "OK", 입력 "0:길이:데이터", 크기 "1:열:행:", keepalive "2".
받은 입력·크기는 'TERM ...' 줄로 로그에 남긴다(UiSnapshot 터미널 검사가 확인한다)."""
import re, sys, threading, time
from vnc import WebSocket, GUID
import base64, hashlib


def log(text):
    sys.stderr.write('TERM %s\n' % text)
    sys.stderr.flush()


PROMPT = '\x1b[1;32mroot@pve1\x1b[0m:\x1b[1;34m~\x1b[0m# '


def banner():
    out = ['Linux pve1 6.14.8-2-pve #1 SMP PREEMPT_DYNAMIC x86_64\r\n\r\n',
           '\x1b[1m가짜 Proxmox 셸\x1b[0m — 한글·색·선 그리기 확인용 (명령: ls, colors, top, seq N, clear)\r\n\r\n']
    return ''.join(out)


def ls():
    return ('\x1b[1;34mbackup\x1b[0m  \x1b[1;34mdump\x1b[0m  \x1b[1;32mrun.sh\x1b[0m  notes.txt  '
            '\x1b[1;36mlink\x1b[0m  \x1b[30;43m공유폴더\x1b[0m  보고서.pdf\r\n')


def colors():
    out = []
    for i in range(16):
        out.append('\x1b[48;5;%dm  ' % i)
    out.append('\x1b[0m\r\n')
    for row in range(6):
        for col in range(36):
            out.append('\x1b[48;5;%dm ' % (16 + row * 36 + col))
        out.append('\x1b[0m\r\n')
    for i in range(24):
        out.append('\x1b[48;5;%dm  ' % (232 + i))
    out.append('\x1b[0m\r\n')
    for i in range(0, 72):
        out.append('\x1b[48;2;%d;%d;%dm ' % (255 - i * 3, i * 3, 128))
    out.append('\x1b[0m\r\n')
    out.append('\x1b[1m굵게\x1b[0m \x1b[3m기울임\x1b[0m \x1b[4m밑줄\x1b[0m \x1b[7m반전\x1b[0m \x1b[9m취소선\x1b[0m '
               '\x1b[2m흐림\x1b[0m \x1b[38;2;255;128;0m24비트\x1b[0m\r\n')
    return ''.join(out)


def top(cols, rows):
    w = max(30, min(cols, 100)) - 2
    out = ['\x1b[?1049h\x1b[?25l\x1b[H\x1b[2J']
    out.append('\x1b[1;1H┌' + '─' * w + '┐')
    title = ' pve1 — CPU · 메모리 '
    out.append('\x1b[1;4H\x1b[1m%s\x1b[0m' % title)
    for r in range(2, min(rows, 12)):
        out.append('\x1b[%d;1H│\x1b[%d;%dH│' % (r, r, w + 2))
    bottom = min(rows, 12)
    out.append('\x1b[%d;1H└' % bottom + '─' * w + '┘')
    bars = [('CPU', 0.62, 32), ('MEM', 0.41, 34), ('SWP', 0.08, 31)]
    for i, (name, v, col) in enumerate(bars):
        n = int((w - 12) * v)
        out.append('\x1b[%d;3H%s [\x1b[%dm%s\x1b[0m%s] %3d%%' % (3 + i, name, col, '█' * n, ' ' * (w - 12 - n), v * 100))
    out.append('\x1b[7;3H\x1b[30;46m  PID USER      CPU%% COMMAND       \x1b[0m')
    out.append('\x1b[8;3H 1234 root      12.0 qemu-system-x86')
    out.append('\x1b[9;3H 5678 www-data   3.1 nginx: 작업자')
    out.append('\x1b[10;3H╔═══╗ ▀▄ ▌▐ ░▒▓')
    out.append('\x1b[11;3H╚═══╝ q: 끝내기')
    return ''.join(out)


def serve(handler):
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
    state = dict(cols=80, rows=24, line='', top=False)

    def send(text):
        ws.send(text.encode('utf-8'))

    try:
        _, auth = ws.read_frame()
        log('auth %s' % auth.decode(errors='replace').strip())
        send('OK')
        send(banner() + PROMPT)
        pending = b''
        while True:
            opcode, payload = ws.read_frame()
            if opcode == 8:
                break
            if opcode == 9 or not payload:
                continue
            pending += payload
            while pending:
                if pending[:1] == b'2':
                    pending = pending[1:]
                    continue
                if pending[:2] == b'1:':
                    parts = pending.split(b':', 3)
                    if len(parts) < 4:
                        break
                    state['cols'], state['rows'] = int(parts[1]), int(parts[2])
                    log('resize %d %d' % (state['cols'], state['rows']))
                    pending = parts[3]
                    continue
                if pending[:2] == b'0:':
                    head, _, rest = pending[2:].partition(b':')
                    n = int(head)
                    if len(rest) < n:
                        break
                    data, pending = rest[:n].decode('utf-8', errors='replace'), rest[n:]
                    log('input %r' % data)
                    handle(data, state, send)
                    continue
                pending = b''
    except (EOFError, ConnectionError, OSError):
        pass
    log('closed')


def handle(data, state, send):
    if state['top']:
        if 'q' in data:
            state['top'] = False
            send('\x1b[?25h\x1b[?1049l' + PROMPT)
        return
    data = re.sub(r'\x1b(\[[0-9;?]*[ -/]*[@-~]|O.|.)', '', data)  # 방향키·기능 키 등은 무시
    for ch in data:
        if ch == '\r':
            cmd = state['line'].strip()
            state['line'] = ''
            send('\r\n')
            run(cmd, state, send)
        elif ch in ('\x7f', '\b'):
            if state['line']:
                last = state['line'][-1]
                state['line'] = state['line'][:-1]
                wide = 2 if ord(last) >= 0x1100 else 1
                send('\b' * wide + ' ' * wide + '\b' * wide)
        elif ch == '\x03':
            state['line'] = ''
            send('^C\r\n' + PROMPT)
        elif ch == '\x0c':
            send('\x1b[H\x1b[2J' + PROMPT + state['line'])
        elif ch == '\x1b' or ord(ch) < 0x20:
            continue
        else:
            state['line'] += ch
            send(ch)


def run(cmd, state, send):
    if cmd == 'ls':
        send(ls())
    elif cmd == 'colors':
        send(colors())
    elif cmd in ('top', 'htop'):
        state['top'] = True
        send(top(state['cols'], state['rows']))
        return
    elif cmd.startswith('seq'):
        n = int(cmd.split()[1]) if len(cmd.split()) > 1 else 10
        send(''.join('%d\r\n' % i for i in range(1, n + 1)))
    elif cmd == 'clear':
        send('\x1b[H\x1b[2J')
    elif cmd == 'size':
        send('%d %d\r\n' % (state['rows'], state['cols']))
    elif cmd:
        send('-bash: %s: 명령을 찾을 수 없습니다\r\n' % cmd)
    send(PROMPT)

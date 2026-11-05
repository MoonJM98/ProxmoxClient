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
        if os.environ.get('FAKEPVE_REAL_SHELL') == '1':
            serve_real(ws, send, log)
            return
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


# ------------------------------------------------------------------ 실제 셸 모드
# FAKEPVE_REAL_SHELL=1 이면 가짜 셸 대신 진짜 bash 를 PTY 로 붙인다(vim·top 같은 실제 프로그램, CT 파일 스크립트 검사).
# pct·lxc-info 는 가짜 명령(심)으로 바꿔 CT 200 의 루트를 /var/lib/lxc/200/rootfs 에 둔다.
import os, pty, fcntl, termios, struct, select, shutil

HERE = os.path.dirname(os.path.abspath(__file__))
SANDBOX = os.environ.get('FAKEPVE_SANDBOX', '/tmp/fakepve-shell')


def prepare_sandbox():
    shims = os.path.join(SANDBOX, 'bin')
    os.makedirs(shims, exist_ok=True)
    with open(os.path.join(shims, 'pct'), 'w') as f:
        f.write('#!/bin/sh\n# 가짜 pct — mount/unmount 는 아무것도 안 한다(루트는 미리 만들어 둔다)\n'
                'case "$1" in mount|unmount) exit 0;; status) echo "status: stopped";; esac\nexit 0\n')
    with open(os.path.join(shims, 'lxc-info'), 'w') as f:
        f.write('#!/bin/sh\n# 꺼진 CT — pid 없음\nexit 0\n')
    for n in ('pct', 'lxc-info'):
        os.chmod(os.path.join(shims, n), 0o755)
    root = '/var/lib/lxc/200/rootfs'
    if not os.path.isdir(root):
        for d in ('etc', 'root', 'var/log', 'home/user/문서', 'srv/www'):
            os.makedirs(os.path.join(root, d), exist_ok=True)
        with open(os.path.join(root, 'etc/passwd'), 'w') as f:
            f.write('root:x:0:0:root:/root:/bin/bash\nuser:x:1000:1000::/home/user:/bin/bash\n')
        with open(os.path.join(root, 'etc/group'), 'w') as f:
            f.write('root:x:0:\nuser:x:1000:\n')
        with open(os.path.join(root, 'etc/hostname'), 'w') as f:
            f.write('ct200\n')
        with open(os.path.join(root, 'home/user/문서/메모.txt'), 'w') as f:
            f.write('안녕하세요\n')
        with open(os.path.join(root, 'var/log/syslog'), 'w') as f:
            f.write('log line\n' * 100)
        os.symlink('/srv/www', os.path.join(root, 'www'))
        os.chown(os.path.join(root, 'home/user'), 1000, 1000)
    os.makedirs(os.path.join(SANDBOX, 'home'), exist_ok=True)
    return shims


def serve_real(ws, send, log):
    shims = prepare_sandbox()
    pid, fd = pty.fork()
    if pid == 0:
        env = dict(os.environ, TERM='xterm-256color', HOME=os.path.join(SANDBOX, 'home'), LANG='C.UTF-8',
                   PATH=shims + ':' + os.environ.get('PATH', '/usr/bin:/bin'), PS1='root@pve1:\\w# ')
        os.chdir(os.path.join(SANDBOX, 'home'))
        # 백그라운드로 띄운 서버는 SIGINT·SIGQUIT 무시를 물려받는다 — 실제 노드 셸처럼 기본 동작으로
        import signal
        for sig in (signal.SIGINT, signal.SIGQUIT, signal.SIGTSTP, signal.SIGTTIN, signal.SIGTTOU, signal.SIGPIPE):
            signal.signal(sig, signal.SIG_DFL)
        os.execvpe('bash', ['bash', '--norc', '--noprofile', '-i'], env)

    def pump():
        try:
            while True:
                r, _, _ = select.select([fd], [], [], 1)
                if fd in r:
                    data = os.read(fd, 65536)
                    if not data:
                        break
                    ws.send(data)
        except OSError:
            pass
        log('pty closed')
        # 실제 termproxy 처럼 셸이 끝나면 웹소켓을 닫는다
        try:
            ws.sock.sendall(bytes([0x88, 0x02, 0x03, 0xE8]))
            ws.sock.shutdown(2)
        except OSError:
            pass

    threading.Thread(target=pump, daemon=True).start()
    pending = b''
    try:
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
                    cols, rows = int(parts[1]), int(parts[2])
                    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack('HHHH', rows, cols, 0, 0))
                    log('resize %d %d' % (cols, rows))
                    pending = parts[3]
                    continue
                if pending[:2] == b'0:':
                    head, _, rest = pending[2:].partition(b':')
                    n = int(head)
                    if len(rest) < n:
                        break
                    data, pending = rest[:n], rest[n:]
                    log('input %r' % data.decode('utf-8', errors='replace')[:4000])
                    os.write(fd, data)
                    continue
                pending = b''
    except (EOFError, ConnectionError, OSError):
        pass
    try:
        os.kill(pid, 9)
        os.waitpid(pid, 0)
    except OSError:
        pass
    log('closed')

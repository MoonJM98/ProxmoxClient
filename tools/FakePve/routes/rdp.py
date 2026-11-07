# RDP 콘솔(rdpproxy, 아직 정식 PVE 에 없는 기능) — 웹소켓 프록시는 ../rdp.py
import re
import rdp

DISPLAY_RDP = set()  # /__fake/rdp 로 디스플레이를 rdp 로 바꾼 VM


@route('POST', r'/nodes/([^/]+)/qemu/(\d+)/rdpproxy')
def rdpproxy(m, query, body):
    vmid = int(m.group(2))
    token = rdp.new_token(vmid)
    rdp.log('rdpproxy vmid=%d user=%s' % (vmid, rdp.CONFIG['user']))
    # 실제 PVE 는 일회성 계정을 만든다 — 가짜는 대상 RDP 서버의 시험 계정을 그대로 준다
    return {'user': rdp.CONFIG['user'], 'password': rdp.CONFIG['password'], 'token': token}


# 시험 설정: 대상 RDP 서버·계정, 그리고 이 VM 의 디스플레이를 rdp 로(설정 vga=type=rdp)
@route('POST', r'/__fake/rdp')
def configure(m, query, body):
    for k in ('target', 'user', 'password'):
        if k in body:
            rdp.CONFIG[k] = body[k]
    if 'vmid' in body:
        DISPLAY_RDP.add(int(body['vmid']))
    rdp.log('config target=%s user=%s vmid=%s' % (rdp.CONFIG['target'], rdp.CONFIG['user'], body.get('vmid')))
    return True


# 열린 RDP 웹소켓을 끊는다(다시 연결 검사)
@route('POST', r'/__fake/rdp-drop')
def drop(m, query, body):
    return rdp.drop_all()


# 설정 읽기: 다른 경로 파일이 주는 설정에 vga=type=rdp 만 덧씌운다(맨 앞에 끼워 두고, 나머지 처리기는 그대로 부른다)
def _config_with_rdp(m, query, body):
    vmid = int(m.group(1))
    base = {}
    for method, rx, fn in STATE.ROUTES:
        if method == 'GET' and fn is not _config_with_rdp and rx.fullmatch(m.string):
            base = fn(rx.fullmatch(m.string), query, body)
            break
    if vmid in DISPLAY_RDP and isinstance(base, dict):
        base = dict(base, vga='type=rdp,clipboard=vnc')
    return base


STATE.ROUTES.insert(0, ('GET', re.compile(r'/nodes/[^/]+/qemu/(\d+)/config'), _config_with_rdp))

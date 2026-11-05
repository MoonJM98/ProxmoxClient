# 앱 설정·콘솔 메뉴(SPICE·RDP) 화면이 부르는 API 의 예시 응답.
#  - POST /nodes/{node}/qemu/{vmid}/spiceproxy : SPICE 연결 정보(클라이언트가 .vv 파일로 만든다). 실제 SPICE 서버는 없다.
#  - GET  /nodes/{node}/qemu/102/config : 디스플레이가 RDP 인 Windows VM. 메인 화면 콘솔 ▾ 에 RDP 항목이 보이게 한다.
#    guest_hardware.py 의 일반 config 경로가 먼저 등록돼 있어 이 항목은 ROUTES 맨 앞에 넣는다(다른 VM 은 그대로 그쪽이 맡는다).
import re

WIN_RDP_CONFIG = {
    'name': 'win11-test', 'ostype': 'win11', 'machine': 'q35', 'bios': 'ovmf',
    'cores': '4', 'sockets': '1', 'memory': '8192', 'cpu': 'host',
    'vga': 'type=rdp,clipboard=vnc', 'scsihw': 'virtio-scsi-single',
    'boot': 'order=scsi0', 'agent': '1',
    'scsi0': 'local-lvm:vm-102-disk-0,discard=on,size=64G,ssd=1',
    'net0': 'virtio=BC:24:11:DD:EE:02,bridge=vmbr0,firewall=1',
    'digest': 'ghi',
}


@route('POST', r'/nodes/([^/]+)/qemu/(\d+)/spiceproxy')
def spiceproxy(m, query, body):
    vmid = int(m.group(2))
    return {
        'type': 'spice', 'host': '192.168.10.10', 'proxy': 'http://192.168.10.10:3128',
        'password': 'fake-spice-ticket', 'tls-port': 61001, 'host-subject': 'CN=pve1',
        'title': f'VM {vmid}', 'delete-this-file': 1,
        'release-cursor': 'Ctrl+Alt_L', 'toggle-fullscreen': 'Shift+F11',
    }


def _front(method, pattern):
    # ROUTES 는 등록 순서대로 맞춰 본다 — 이 경로만 앞에 끼워 넣는다
    def deco(fn):
        STATE.ROUTES.insert(0, (method, re.compile(pattern), fn))
        return fn
    return deco


@_front('GET', r'/nodes/([^/]+)/qemu/102/config')
def rdp_vm_config(m, query, body):
    return dict(WIN_RDP_CONFIG)

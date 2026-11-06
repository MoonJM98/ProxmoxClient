# 게스트 탐색 화면(Views/Guest)이 부르는 API 의 예시 응답 — 요약·이전·에이전트·Cloud-Init·복제.
import time

_UPID = 'UPID:pve1:0000B1C2:0013D4E5:{t:08X}:{typ}:{vid}:root@pam:'

def _upid(node, typ, vid):
    return _UPID.format(t=int(time.time()), typ=typ, vid=vid).replace('pve1', node)

def _agent_result(value):
    # PVE 에이전트 응답은 {"result": …} 로 감싸 온다
    return {'result': value}

# 복제 — 새 VMID 의 작업(UPID)을 돌려준다
@route('POST', r'/nodes/(\w+)/(qemu|lxc)/(\d+)/clone')
def clone(m, query, body):
    # 실서버처럼 이름 칸을 검사한다 — VM 은 name, CT 는 hostname(다른 쪽을 보내면 400)
    wrong = 'hostname' if m.group(2) == 'qemu' else 'name'
    if wrong in body:
        raise STATE.PveError(400, 'Parameter verification failed.\n'
                             f'{wrong}: property is not defined in schema and the schema does not allow additional properties\n')
    return _upid(m.group(1), 'qmclone' if m.group(2) == 'qemu' else 'vzclone', m.group(3))

# 이전 사전 확인 — 허용 노드, 옮길 수 없는 노드와 이유, 로컬 디스크
@route('GET', r'/nodes/(\w+)/(qemu|lxc)/(\d+)/migrate')
def migrate_check(m, query, body):
    return {
        'running': 0,
        'allowed_nodes': ['pve2'],
        'not_allowed_nodes': {'pve3': {'unavailable-storages': 'local-zfs'}},
        'local_disks': [{'volid': 'local-lvm:vm-100-disk-1', 'size': 8589934592, 'cdrom': 0}],
        'local_resources': [],
    }

# 게스트 에이전트 — 요약(호스트 이름·OS·NIC)과 읽기 전용 정보 명령
@route('GET', r'/nodes/(\w+)/qemu/(\d+)/agent/get-host-name')
def agent_host(m, query, body):
    return _agent_result({'host-name': 'web-01'})

@route('GET', r'/nodes/(\w+)/qemu/(\d+)/agent/get-osinfo')
def agent_os(m, query, body):
    return _agent_result({'pretty-name': 'Debian GNU/Linux 12 (bookworm)', 'name': 'Debian', 'kernel-release': '6.1.0-18-amd64'})

@route('GET', r'/nodes/(\w+)/qemu/(\d+)/agent/get-fsinfo')
def agent_fsinfo(m, query, body):
    return _agent_result([{'name': 'sda1', 'mountpoint': '/', 'type': 'ext4', 'total-bytes': 34359738368, 'used-bytes': 8589934592}])

@route('GET', r'/nodes/(\w+)/qemu/(\d+)/agent/get-users')
def agent_users(m, query, body):
    return _agent_result([{'user': 'root', 'login-time': 1700000000.0}])

@route('GET', r'/nodes/(\w+)/qemu/(\d+)/agent/get-time')
def agent_time(m, query, body):
    return _agent_result(1700000000)

@route('GET', r'/nodes/(\w+)/qemu/(\d+)/agent/get-timezone')
def agent_tz(m, query, body):
    return _agent_result({'zone': 'Asia/Seoul', 'offset': 32400})

@route('GET', r'/nodes/(\w+)/qemu/(\d+)/agent/get-vcpus')
def agent_vcpus(m, query, body):
    return _agent_result([{'logical-id': 0, 'online': True, 'can-offline': False}])

@route('GET', r'/nodes/(\w+)/qemu/(\d+)/agent/get-memory-block-info')
def agent_mem_info(m, query, body):
    return _agent_result({'size': 134217728, 'removable': False})

@route('GET', r'/nodes/(\w+)/qemu/(\d+)/agent/get-memory-blocks')
def agent_mem_blocks(m, query, body):
    return _agent_result([{'phys-index': 0, 'online': True, 'removable': False}])

@route('GET', r'/nodes/(\w+)/qemu/(\d+)/agent/info')
def agent_info(m, query, body):
    return _agent_result({'version': '9.0.10', 'supported_commands': [{'name': 'guest-ping', 'enabled': True}]})

# 명령 실행 — 처음 상태 조회에서 끝난 것으로 답한다
@route('POST', r'/nodes/(\w+)/qemu/(\d+)/agent/exec')
def agent_exec(m, query, body):
    return {'pid': 4242}

@route('GET', r'/nodes/(\w+)/qemu/(\d+)/agent/exec-status')
def agent_exec_status(m, query, body):
    return {'exited': 1, 'exitcode': 0, 'out-data': 'hello from web-01\n', 'out-truncated': 0,
            'err-data': '', 'err-truncated': 0}

@route('GET', r'/nodes/(\w+)/qemu/(\d+)/agent/file-read')
def agent_file_read(m, query, body):
    return {'content': '# /etc/hostname\nweb-01\n', 'truncated': 0}

@route('POST', r'/nodes/(\w+)/qemu/(\d+)/agent/file-write')
def agent_file_write(m, query, body):
    return None

@route('POST', r'/nodes/(\w+)/qemu/(\d+)/agent/ping')
def agent_ping(m, query, body):
    return None

@route('POST', r'/nodes/(\w+)/qemu/(\d+)/agent/fstrim')
def agent_fstrim(m, query, body):
    return _upid(m.group(1), 'qmfstrim', m.group(2))

@route('POST', r'/nodes/(\w+)/qemu/(\d+)/agent/set-user-password')
def agent_password(m, query, body):
    return None

@route('POST', r'/nodes/(\w+)/qemu/(\d+)/agent/(freeze|thaw)')
def agent_freeze(m, query, body):
    return 1

@route('GET', r'/nodes/(\w+)/qemu/(\d+)/agent/fsfreeze-status')
def agent_freeze_status(m, query, body):
    return 'thawed'

# Cloud-Init — 적용 대기 값과 서버가 만든 설정 원문
@route('GET', r'/nodes/(\w+)/qemu/(\d+)/cloudinit')
def cloudinit_pending(m, query, body):
    return [
        {'key': 'ipconfig0', 'value': 'ip=192.168.10.21/24,gw=192.168.10.1', 'pending': 'ip=192.168.10.22/24,gw=192.168.10.1'},
        {'key': 'ciuser', 'value': 'admin', 'pending': ''},
    ]

@route('GET', r'/nodes/(\w+)/qemu/(\d+)/cloudinit/dump')
def cloudinit_dump(m, query, body):
    kind = query.get('type', 'user')
    return {
        'user': '#cloud-config\nusers:\n  - name: admin\n    sudo: ALL=(ALL) NOPASSWD:ALL\n',
        'network': 'version: 1\nconfig:\n  - type: physical\n    name: eth0\n',
        'meta': 'instance-id: vm-100\nlocal-hostname: web-01\n',
    }.get(kind, '')

# 클론 화면이 고를 대상 노드 목록
@route('GET', r'/nodes')
def nodes(m, query, body):
    return [{'node': 'pve1', 'status': 'online'}, {'node': 'pve2', 'status': 'online'}]

# 게스트 방화벽의 별칭·IP 집합·로그(방화벽 탭 하위 화면)
@route('GET', r'/nodes/(\w+)/(qemu|lxc)/(\d+)/firewall/aliases')
def guest_fw_aliases(m, query, body):
    return [{'name': 'lan', 'cidr': '192.168.10.0/24', 'comment': '사무실 대역'}]

@route('GET', r'/nodes/(\w+)/(qemu|lxc)/(\d+)/firewall/ipset')
def guest_fw_ipset(m, query, body):
    return [{'name': 'trusted', 'comment': '관리 PC'}]

@route('GET', r'/nodes/(\w+)/(qemu|lxc)/(\d+)/firewall/log')
def guest_fw_log(m, query, body):
    return [{'n': 1, 't': 'ACCEPT tcp 192.168.10.5:51234 -> 192.168.10.21:22'},
            {'n': 2, 't': 'DROP udp 203.0.113.9:5353 -> 192.168.10.21:5353'}]

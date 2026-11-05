# 게스트 탭(요약·모니터·옵션·스냅숏·백업·작업)이 부르는 API 의 예시 응답 — VM 100(web-01, pve1) · CT 200(dns, pve2)
import math
import random
import time

_NOW = int(time.time())
_STEP = {'hour': 60, 'day': 1800, 'week': 7200, 'month': 28800, 'year': 604800}

# 옵션 탭의 pending 응답 행(key·value·pending). pending 이 value 와 다르면 "대기 중" 줄로 보인다.
_VM_CONFIG = [
    ('name', 'web-01'), ('onboot', '1'), ('startup', 'order=1,up=30,down=60'), ('ostype', 'l26'),
    ('boot', 'order=scsi0;net0'), ('bootdisk', 'scsi0'), ('tablet', '0'), ('hotplug', 'disk,network,usb'),
    ('acpi', '1'), ('kvm', '1'), ('localtime', '1'), ('agent', '1,fstrim_cloned_disks=1'),
    ('smbios1', 'manufacturer=ProxmoxSample,product=web-01,base64=0'), ('protection', '0'),
    ('description', 'web server (production)'), ('cores', '4'), ('memory', '4096'),
    ('scsi0', 'local-lvm:vm-100-disk-0,iothread=1,size=32G'),
    ('net0', 'virtio=BC:24:11:AA:BB:CC,bridge=vmbr0,firewall=1'),
]
_VM_PENDING_CHANGES = {'hotplug': 'disk,network,usb,memory,cpu'}

_CT_CONFIG = [
    ('hostname', 'dns'), ('onboot', '1'), ('startup', 'order=2'), ('ostype', 'debian'), ('arch', 'amd64'),
    ('unprivileged', '1'), ('features', 'nesting=1'), ('tty', '2'), ('cmode', 'tty'), ('console', '1'),
    ('protection', '0'), ('nameserver', '192.168.10.1'), ('searchdomain', 'lan'),
    ('description', 'internal DNS (unbound)'), ('cores', '1'), ('memory', '512'),
    ('net0', 'name=eth0,bridge=vmbr0,gw=192.168.10.1,ip=192.168.10.53/24,hwaddr=BC:24:11:CC:DD:EE,type=veth'),
    ('rootfs', 'local-lvm:vm-200-disk-0,size=8G'),
]

_STORAGES = {
    'local': dict(storage='local', type='dir', content='iso,vztmpl,backup', active=1, enabled=1, shared=0),
    'local-lvm': dict(storage='local-lvm', type='lvmthin', content='images,rootdir', active=1, enabled=1, shared=0),
    'nas': dict(storage='nas', type='nfs', content='backup,iso', active=1, enabled=1, shared=1),
}
_NODE_STORAGES = {'pve1': ['local', 'local-lvm'], 'pve2': ['nas']}

_BACKUPS = [
    dict(volid='local:backup/vzdump-qemu-100-2026_11_03-02_00_12.vma.zst', storage='local', content='backup',
         format='vma.zst', size=2254857830, ctime=_NOW - 2 * 86400 - 3 * 3600, vmid=100, notes='nightly', protected=1),
    dict(volid='local:backup/vzdump-qemu-100-2026_11_01-02_00_09.vma.zst', storage='local', content='backup',
         format='vma.zst', size=2190443110, ctime=_NOW - 4 * 86400 - 3 * 3600, vmid=100, notes='', protected=0),
    dict(volid='local:backup/vzdump-lxc-200-2026_11_02-02_30_44.tar.zst', storage='local', content='backup',
         format='tar.zst', size=612345678, ctime=_NOW - 3 * 86400 - 3 * 3600, vmid=200, notes='dns before upgrade',
         protected=0),
    dict(volid='nas:backup/vzdump-qemu-100-2026_10_28-03_00_01.vma.zst', storage='nas', content='backup',
         format='vma.zst', size=2301234567, ctime=_NOW - 9 * 86400, vmid=100, notes='monthly copy', protected=0),
    dict(volid='nas:backup/vzdump-lxc-200-2026_10_30-03_00_01.tar.zst', storage='nas', content='backup',
         format='tar.zst', size=598765432, ctime=_NOW - 7 * 86400, vmid=200, notes='', protected=0),
]

_SNAPSHOTS = {
    100: [
        dict(name='weekly-2026-10', description='주간 백업 전', snaptime=_NOW - 9 * 86400, running=1,
             parent=None),
        dict(name='before-upgrade', description='apt full-upgrade 전', snaptime=_NOW - 2 * 86400, running=0,
             parent='weekly-2026-10'),
        dict(name='current', description='You are here!', parent='before-upgrade'),
    ],
}

_MONITOR = {
    'info status': 'VM status: running',
    'info block': 'drive-scsi0 (#block112): local-lvm:vm-100-disk-0 (raw, read-write)\n'
                  '    Attached to:      scsi0\n'
                  '    Cache mode:       writeback',
    'info version': '9.0.10-pve (QEMU 9.2.0)',
}


def _upid(node, typ, vmid, user='root@pam'):
    return f"UPID:{node}:0000B1C2:0013D4E5:{int(time.time()):08X}:{typ}:{vmid}:{user}:"


def _rrd_samples(vmid, timeframe):
    step = _STEP.get(timeframe, 60)
    rnd = random.Random(vmid * 7919 + step)
    end = _NOW - _NOW % step
    samples = []
    for i in range(60):
        t = end - (59 - i) * step
        x = i / 60.0 * 2 * math.pi
        if vmid == 100:  # web-01: 4 vCPU, 4 GiB
            cpu = 0.14 + 0.07 * math.sin(x * 2) + rnd.uniform(-0.02, 0.02)
            mem = 2.3e9 + 1.5e8 * math.sin(x) + rnd.uniform(-5e6, 5e6)
            maxmem = 4294967296
            netin = 120000 + 60000 * abs(math.sin(x * 3)) + rnd.uniform(0, 20000)
            netout = 45000 + 25000 * abs(math.sin(x * 5)) + rnd.uniform(0, 8000)
            diskread = max(0.0, 900000 + 700000 * math.sin(x * 4)) + rnd.uniform(0, 50000)
            diskwrite = max(0.0, 300000 + 200000 * math.sin(x * 6)) + rnd.uniform(0, 30000)
        else:  # dns: 1 vCPU, 512 MiB
            cpu = 0.015 + 0.01 * math.sin(x * 3) + rnd.uniform(0, 0.005)
            mem = 6.2e7 + 4e6 * math.sin(x * 2) + rnd.uniform(-1e6, 1e6)
            maxmem = 536870912
            netin = 4000 + 2500 * abs(math.sin(x * 2)) + rnd.uniform(0, 500)
            netout = 6000 + 3000 * abs(math.sin(x * 3)) + rnd.uniform(0, 500)
            diskread = max(0.0, 20000 * math.sin(x * 4) ** 2) + rnd.uniform(0, 2000)
            diskwrite = max(0.0, 9000 * abs(math.sin(x * 5))) + rnd.uniform(0, 1000)
        samples.append({'time': t, 'cpu': round(cpu, 5), 'mem': round(mem), 'maxmem': maxmem,
                        'netin': round(netin), 'netout': round(netout), 'diskread': round(diskread),
                        'diskwrite': round(diskwrite)})
    return samples


def _pending_rows(vmid):
    config = _VM_CONFIG if vmid == 100 else _CT_CONFIG
    changes = _VM_PENDING_CHANGES if vmid == 100 else {}
    rows = []
    for key, value in config:
        row = {'key': key, 'value': value}
        if key in changes:
            row['pending'] = changes[key]
        rows.append(row)
    return rows


# ---- 게스트 RRD(요약 그래프)
@route('GET', r'/nodes/([^/]+)/qemu/(\d+)/rrddata')
def rrd_vm(m, query, body):
    return _rrd_samples(int(m.group(2)), query.get('timeframe', 'hour'))


@route('GET', r'/nodes/([^/]+)/lxc/(\d+)/rrddata')
def rrd_ct(m, query, body):
    return _rrd_samples(int(m.group(2)), query.get('timeframe', 'hour'))


# ---- 옵션 탭: 현재 값 + 대기 중 변경
@route('GET', r'/nodes/([^/]+)/(qemu|lxc)/(\d+)/pending')
def pending(m, query, body):
    return _pending_rows(int(m.group(3)))


@route('GET', r'/nodes/([^/]+)/(qemu|lxc)/(\d+)/config')
def config(m, query, body):
    return {row['key']: row['value'] for row in _pending_rows(int(m.group(3)))}


@route('PUT', r'/nodes/([^/]+)/(qemu|lxc)/(\d+)/config')
def save_config(m, query, body):
    return None


# ---- 모니터: QEMU 모니터 명령 출력
@route('POST', r'/nodes/([^/]+)/qemu/(\d+)/monitor')
def monitor(m, query, body):
    return _MONITOR.get(body.get('command', ''), 'unknown command')


# ---- 스냅숏
@route('GET', r'/nodes/([^/]+)/qemu/(\d+)/snapshot')
def snapshots(m, query, body):
    return _SNAPSHOTS.get(int(m.group(2)), [dict(name='current', description='You are here!')])


@route('POST', r'/nodes/([^/]+)/qemu/(\d+)/snapshot')
def create_snapshot(m, query, body):
    return _upid(m.group(1), 'qmsnapshot', m.group(2))


@route('POST', r'/nodes/([^/]+)/qemu/(\d+)/snapshot/([^/]+)/rollback')
def rollback_snapshot(m, query, body):
    return _upid(m.group(1), 'qmrollback', m.group(2))


@route('DELETE', r'/nodes/([^/]+)/qemu/(\d+)/snapshot/([^/]+)')
def delete_snapshot(m, query, body):
    return _upid(m.group(1), 'qmdelsnapshot', m.group(2))


@route('PUT', r'/nodes/([^/]+)/qemu/(\d+)/snapshot/([^/]+)/config')
def snapshot_note(m, query, body):
    return None


# ---- 백업: 저장소·백업 파일·노드 기본값·설정 원문
@route('GET', r'/nodes/([^/]+)/storage')
def node_storages(m, query, body):
    names = _NODE_STORAGES.get(m.group(1), [])
    wanted = query.get('content')
    rows = [dict(_STORAGES[n]) for n in names]
    if wanted:
        rows = [r for r in rows if wanted in r['content'].split(',')]
    return rows


@route('GET', r'/nodes/([^/]+)/storage/([^/]+)/content')
def storage_content(m, query, body):
    rows = [b for b in _BACKUPS if b['storage'] == m.group(2)]
    if query.get('content'):
        rows = [b for b in rows if b['content'] == query['content']]
    if query.get('vmid'):
        rows = [b for b in rows if b['vmid'] == int(query['vmid'])]
    return [dict(b) for b in rows]


@route('PUT', r'/nodes/([^/]+)/storage/([^/]+)/content/(.+)')
def update_volume(m, query, body):
    return None


@route('DELETE', r'/nodes/([^/]+)/storage/([^/]+)/content/(.+)')
def delete_volume(m, query, body):
    return _upid(m.group(1), 'imgdel', m.group(2))


@route('GET', r'/nodes/([^/]+)/vzdump/defaults')
def vzdump_defaults(m, query, body):
    return {'storage': 'local', 'mode': 'snapshot', 'compress': 'zstd', 'notification-mode': 'auto',
            'notes-template': '{{guestname}}'}


@route('GET', r'/nodes/([^/]+)/vzdump/extractconfig')
def extract_config(m, query, body):
    return ('arch: amd64\ncores: 4\nmemory: 4096\nname: web-01\nnet0: virtio=BC:24:11:AA:BB:CC,bridge=vmbr0\n'
            'scsi0: local-lvm:vm-100-disk-0,iothread=1,size=32G\nostype: l26\n')


@route('POST', r'/nodes/([^/]+)/vzdump')
def backup_now(m, query, body):
    return _upid(m.group(1), 'vzdump', body.get('vmid', ''))


# ---- 작업 기록: 노드의 작업 중 이 게스트 것만(vmid 필터). 실행 중인 작업 하나를 섞어 중지 버튼도 볼 수 있게 한다.
_RUNNING = {'upid': _upid('pve1', 'vzdump', '100'), 'node': 'pve1', 'type': 'vzdump', 'id': '100',
            'user': 'root@pam', 'starttime': _NOW - 45, 'status': 'running'}


@route('GET', r'/nodes/([^/]+)/tasks')
def node_tasks(m, query, body):
    node = m.group(1)
    vmid = query.get('vmid')
    rows = [dict(_RUNNING)] if node == 'pve1' else []
    rows += [dict(task) for task in STATE.TASKS if task.get('node') == node]  # fake_pve.py 의 작업 목록
    if vmid is not None:
        rows = [r for r in rows if str(r.get('id', '')) == vmid]
    return rows

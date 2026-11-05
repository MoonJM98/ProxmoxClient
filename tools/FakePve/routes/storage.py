# 저장소 화면(요약·사용량 그래프·백업·ISO·CT 템플릿·가져오기·디스크 이미지)과 저장소 작업이 부르는 API 의 예시 응답.
#
# 같은 경로(/nodes/x/storage/y/content)를 guest_hardware.py 도 가진다. 그 쪽은 하드웨어 화면의 ISO 목록뿐이라
# 여기서는 STATE.ROUTES 맨 앞에 등록해 이 파일이 답하게 한다(ISO 항목은 그 쪽과 같은 것을 포함한다).
import base64
import math
import re
import time

NOW = int(time.time())
DAY = 86400

GB = 1_000_000_000


def front(method, pattern):
    """첫 번째로 맞는 경로가 답하므로 다른 파일보다 앞에 둔다."""
    def deco(fn):
        STATE.ROUTES.insert(0, (method, re.compile(pattern), fn))
        return fn
    return deco


# ---- 권한: core.py 의 관리자 권한 목록에 템플릿 권한(Datastore.AllocateTemplate)을 더한다 ----
# 올리기·URL 에서 받기·CT 템플릿·OCI 버튼은 이 권한이 있어야 보이므로(WPF 와 같은 조건), 그 버튼을 검증하려면 필요하다.
_core_permissions = next(fn for m, rx, fn in STATE.ROUTES if m == 'GET' and rx.pattern == '/access/permissions')


@front('GET', r'/access/permissions')
def permissions(m, query, body):
    result = _core_permissions(m, query, body)
    for privs in result.values():
        privs['Datastore.AllocateTemplate'] = 1
    return result


# ---- 저장소 상태(요약 탭) — 저장소 이름별 ----
STATUS = {
    'local': dict(type='dir', active=1, enabled=1, shared=0, content='iso,vztmpl,backup',
                  total=100 * GB, used=40 * GB, avail=60 * GB),
    'local-lvm': dict(type='lvmthin', active=1, enabled=1, shared=0, content='images,rootdir',
                      total=400 * GB, used=300 * GB, avail=100 * GB),
    'nas': dict(type='nfs', active=1, enabled=1, shared=1, content='backup,iso',
                total=4000 * GB, used=1500 * GB, avail=2500 * GB),
    'pbs-main': dict(type='pbs', active=1, enabled=1, shared=1, content='backup',
                     total=2000 * GB, used=800 * GB, avail=1200 * GB),
}

# ---- 저장소 설정(GET /storage/{id}) — 정리 규칙 칸을 채울 때 쓴다 ----
CONFIG = {
    'local': dict(storage='local', type='dir', path='/var/lib/vz', content='iso,vztmpl,backup',
                  shared=0, **{'prune-backups': {'keep-last': 3, 'keep-weekly': 2}}),
    'local-lvm': dict(storage='local-lvm', type='lvmthin', vgname='pve', thinpool='data', content='images,rootdir'),
    'nas': dict(storage='nas', type='nfs', server='192.168.10.5', export='/volume1/proxmox',
                content='backup,iso,vztmpl', shared=1, nodes='pve1,pve2'),
    'pbs-main': dict(storage='pbs-main', type='pbs', server='pbs.lab.example', datastore='main', content='backup'),
}


def _rrd(storage, timeframe):
    """사용량 시계열 — 기간마다 간격을 달리해 최근으로 올수록 조금씩 늘어나는 모양."""
    step = {'hour': 60, 'day': 1800, 'week': 12600, 'month': 43200, 'year': 604800}.get(timeframe, 60)
    total = STATUS.get(storage, STATUS['local'])['total']
    base = STATUS.get(storage, STATUS['local'])['used']
    rows = []
    for i in range(70):
        t = NOW - (69 - i) * step
        used = base * (0.92 + 0.08 * i / 69) + base * 0.02 * math.sin(i / 5.0)
        rows.append(dict(time=t, used=int(used), total=total, maxdisk=total))
    return rows


@front('GET', r'/nodes/([^/]+)/storage/([^/]+)/status')
def storage_status(m, query, body):
    s = STATUS.get(m.group(2), STATUS['local'])
    return dict(s)


@front('GET', r'/nodes/([^/]+)/storage/([^/]+)/rrddata')
def storage_rrd(m, query, body):
    return _rrd(m.group(2), query.get('timeframe', 'hour'))


def _backup(volid, vmid, fmt, size, age_days, protected=0, notes=''):
    return dict(volid=volid, content='backup', vmid=vmid, format=fmt, size=size,
                ctime=NOW - int(age_days * DAY), protected=protected, notes=notes)


CONTENT = {
    ('local', 'backup'): [
        _backup('local:backup/vzdump-qemu-100-2026_11_03-02_00_00.vma.zst', 100, 'vma.zst', 3_412_000_000, 2.1,
                protected=1, notes='web-01 주간'),
        _backup('local:backup/vzdump-lxc-200-2026_11_04-02_00_00.tar.zst', 200, 'tar.zst', 512_000_000, 1,
                notes='dns'),
        _backup('local:backup/vzdump-qemu-100-2026_11_01-02_00_00.vma.zst', 100, 'vma.zst', 3_398_000_000, 4),
        _backup('local:backup/vzdump-qemu-101-2026_10_28-02_00_00.vma.zst', 101, 'vma.zst', 8_120_000_000, 8,
                notes='db 백업'),
    ],
    ('nas', 'backup'): [
        _backup('nas:backup/vzdump-qemu-100-2026_11_02-02_00_00.vma.zst', 100, 'vma.zst', 3_405_000_000, 3),
        _backup('nas:backup/vzdump-qemu-102-2026_11_02-02_00_00.vma.zst', 102, 'vma.zst', 1_220_000_000, 3),
    ],
    ('pbs-main', 'backup'): [
        _backup('pbs-main:backup/vm/100/2026-11-03T02:00:00Z', 100, 'pbs-ct', 3_412_000_000, 2.1,
                notes='PBS 스냅샷'),
        _backup('pbs-main:backup/ct/200/2026-11-04T02:00:00Z', 200, 'pbs-ct', 512_000_000, 1),
    ],
    ('local', 'iso'): [
        dict(volid='local:iso/debian-12.12.0-amd64-netinst.iso', content='iso', format='iso', size=661651456,
             ctime=NOW - 30 * DAY),
        dict(volid='local:iso/ubuntu-24.04.2-live-server-amd64.iso', content='iso', format='iso',
             size=3218231296, ctime=NOW - 12 * DAY),
        dict(volid='local:iso/proxmox-ve_9.0-1.iso', content='iso', format='iso', size=1_580_000_000,
             ctime=NOW - 3 * DAY),
    ],
    ('nas', 'iso'): [
        dict(volid='nas:iso/virtio-win-0.1.262.iso', content='iso', format='iso', size=528482304,
             ctime=NOW - 60 * DAY),
        dict(volid='nas:iso/almalinux-9.5-x86_64-minimal.iso', content='iso', format='iso', size=1_950_000_000,
             ctime=NOW - 20 * DAY),
    ],
    ('local', 'vztmpl'): [
        dict(volid='local:vztmpl/debian-12-standard_12.7-1_amd64.tar.zst', content='vztmpl', format='tar.zst',
             size=130_000_000, ctime=NOW - 40 * DAY),
        dict(volid='local:vztmpl/ubuntu-24.04-standard_24.04-2_amd64.tar.zst', content='vztmpl', format='tar.zst',
             size=142_000_000, ctime=NOW - 15 * DAY),
    ],
    ('local', 'import'): [
        dict(volid='local:import/esxi-web-01.ova', content='import', format='ova', size=2_100_000_000,
             ctime=NOW - 2 * DAY),
        dict(volid='local:import/legacy-db.qcow2', content='import', format='qcow2', size=9_800_000_000,
             ctime=NOW - 5 * DAY),
    ],
    ('local', 'images'): [
        dict(volid='local:100/vm-100-disk-1.qcow2', content='images', format='qcow2', vmid=100,
             size=34_359_738_368, ctime=NOW - 90 * DAY),
    ],
    ('local-lvm', 'images'): [
        dict(volid='local-lvm:vm-100-disk-0', content='images', format='raw', vmid=100, size=34_359_738_368,
             ctime=NOW - 90 * DAY),
        dict(volid='local-lvm:vm-100-disk-1', content='images', format='raw', vmid=100, size=536_870_912,
             ctime=NOW - 90 * DAY),
        dict(volid='local-lvm:vm-101-disk-0', content='images', format='raw', vmid=101, size=107_374_182_400,
             ctime=NOW - 30 * DAY),
    ],
    ('local-lvm', 'rootdir'): [
        dict(volid='local-lvm:vm-200-disk-0', content='rootdir', format='raw', vmid=200, size=8_589_934_592,
             ctime=NOW - 60 * DAY),
    ],
}


@front('GET', r'/nodes/([^/]+)/storage/([^/]+)/content')
def storage_content(m, query, body):
    storage, want = m.group(2), query.get('content', '')
    return list(CONTENT.get((storage, want), []))


@front('GET', r'/nodes/([^/]+)/storage/([^/]+)/prunebackups')
def prune_preview(m, query, body):
    """정리 미리 보기 — 최근 3개와 주간 2개만 남기는 규칙처럼 나머지를 remove 로 표시한다."""
    vmid = query.get('vmid')
    rows = [r for r in CONTENT.get((m.group(2), 'backup'), [])
            if vmid is None or str(r.get('vmid')) == vmid]
    rows.sort(key=lambda r: r['ctime'], reverse=True)
    return [dict(volid=r['volid'], vmid=r.get('vmid'), ctime=r['ctime'], type='qemu',
                 mark=('keep' if i < 2 or r.get('protected') else 'remove'))
            for i, r in enumerate(rows)]


@front('GET', r'/storage/([^/]+)')
def storage_config(m, query, body):
    return dict(CONFIG.get(m.group(1), CONFIG['local']))


@front('GET', r'/nodes/([^/]+)/aplinfo')
def templates(m, query, body):
    return [
        dict(section='system', template='debian-12-standard_12.7-1_amd64.tar.zst',
             headline='Debian 12 Bookworm (standard)', os='debian', version='12.7-1', type='ct'),
        dict(section='system', template='ubuntu-24.04-standard_24.04-2_amd64.tar.zst',
             headline='Ubuntu 24.04 LTS (standard)', os='ubuntu', version='24.04-2', type='ct'),
        dict(section='turnkeylinux', template='wordpress-18.0-1-debian-12-amd64.tar.gz',
             headline='WordPress 18.0 (TurnKey)', os='debian', version='18.0-1', type='ct'),
    ]


@front('GET', r'/nodes/([^/]+)/query-url-metadata')
def url_metadata(m, query, body):
    return dict(filename='alpine-3.21-standard.tar.xz', size='3221225', mimetype='application/x-xz')


@front('GET', r'/nodes/([^/]+)/query-oci-repo-tags')
def oci_tags(m, query, body):
    return '3.21,3.20,3.19,latest'


def _b64(path):
    return base64.b64encode(path.encode()).decode()


@front('GET', r'/nodes/([^/]+)/storage/([^/]+)/file-restore/list')
def file_restore_list(m, query, body):
    """PBS 백업 속 파일 — 맨 위는 두 폴더와 파일 하나, 폴더 안은 파일 두 개."""
    base64_path = query.get('filepath', '/')
    try:
        here = base64.b64decode(base64_path).decode() if base64_path != '/' else '/'
    except Exception:
        here = '/'
    if here == '/':
        children = [('etc', 'd', 0), ('home', 'd', 0), ('hosts', 'f', 240)]
    else:
        children = [('hostname', 'f', 9), ('resolv.conf', 'f', 118)]
    rows = []
    for name, kind, size in children:
        path = (here.rstrip('/') + '/' + name) if here != '/' else '/' + name
        rows.append(dict(text=name + ('/' if kind == 'd' else ''), filepath=_b64(path), type=kind,
                         leaf=1 if kind == 'f' else 0, size=size, mtime=NOW - 3600))
    return rows


@front('GET', r'/nodes/([^/]+)/vzdump/extractconfig')
def backup_config(m, query, body):
    return ('agent: 1\ncores: 4\nmemory: 4096\nname: web-01\nnet0: virtio=BC:24:11:AA:BB:CC,bridge=vmbr0\n'
            'scsi0: local-lvm:vm-100-disk-0,size=32G\nsockets: 1\n')


@front('GET', r'/nodes/([^/]+)/storage/([^/]+)/import-metadata')
def import_metadata(m, query, body):
    return {
        'create-args': {'name': 'esxi-web-01', 'memory': '4096', 'cores': '2', 'ostype': 'l26'},
        'disks': {'scsi0': 'local-lvm:vm-105-disk-0'},
        'net': {'net0': {'macaddr': '00:50:56:AA:BB:CC', 'model': 'vmxnet3'}},
        'warnings': [{'type': 'unsupported', 'key': 'sata0'}],
    }


@front('GET', r'/cluster/nextid')
def next_vmid(m, query, body):
    return 105

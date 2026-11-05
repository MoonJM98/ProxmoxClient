"""데이터센터 클러스터 화면(요약·저장소·백업 일정·복제·옵션·CPU 모델·클러스터 로그)의 예시 응답.

/cluster/resources 와 /cluster/tasks 는 fake_pve.py 가 답하므로 여기서 다시 만들지 않는다(검색 탭은 그것을 쓴다).
/version 은 기본 9.0.10 으로 두어 다른 화면과 같다. CPU 모델(9.2+) 화면을 검증할 때만
DC_CLUSTER_PVE_VERSION=9.2.1 을 넣어 서버를 띄운다.
"""
import os
import time

NOW = int(time.time())


@route('GET', r'/version')
def version(m, query, body):
    v = os.environ.get('DC_CLUSTER_PVE_VERSION', '9.0.10')
    return dict(version=v, release='.'.join(v.split('.')[:2]), repoid='abcdef')


@route('GET', r'/cluster/status')
def cluster_status(m, query, body):
    return [
        dict(name='pve-lab', type='cluster', version=3, nodes=2, quorate=1),
        dict(name='pve1', type='node', nodeid=1, ip='192.168.10.11', online=1, local=1),
        dict(name='pve2', type='node', nodeid=2, ip='192.168.10.12', online=1, local=0),
    ]


@route('GET', r'/storage')
def storage_list(m, query, body):
    return [
        dict(storage='local', type='dir', path='/var/lib/vz', content='iso,vztmpl,backup,images',
             shared=0, disable=0, nodes=''),
        dict(storage='local-lvm', type='lvmthin', vgname='pve', thinpool='data', content='images,rootdir',
             shared=0, disable=0, nodes=''),
        dict(storage='nas', type='nfs', server='192.168.10.5', export='/volume1/proxmox',
             content='backup,iso,vztmpl', shared=1, disable=0, nodes='pve1,pve2'),
        dict(storage='pbs-main', type='pbs', server='pbs.lab.example', datastore='main', content='backup',
             shared=1, disable=0, nodes=''),
        dict(storage='archive', type='dir', path='/mnt/archive', content='backup', shared=0, disable=1,
             nodes='pve2'),
    ]


@route('GET', r'/cluster/backup')
def backup_jobs(m, query, body):
    return [
        dict(id='backup-weekly-all', enabled=1, schedule='sat 02:00', **{'next-run': NOW + 86400 * 2},
             storage='nas', node='', all=1, exclude='9000', mode='snapshot',
             comment='Weekly full backup (templates excluded)'),
        dict(id='backup-db-daily', enabled=1, schedule='21:00', **{'next-run': NOW + 3600 * 5},
             storage='nas', node='pve1', vmid='101', mode='snapshot', comment='Database, daily'),
        dict(id='backup-prod-pool', enabled=0, schedule='mon..fri 23:30', **{'next-run': NOW + 86400},
             storage='pbs-main', node='', pool='prod', mode='stop', comment=''),
    ]


@route('GET', r'/cluster/backup/([^/]+)')
def backup_job(m, query, body):
    return {
        'storage': 'nas', 'schedule': 'sat 02:00', 'all': 1, 'exclude': '9000', 'mode': 'snapshot',
        'compress': 'zstd', 'enabled': 1, 'notes-template': '{{guestname}}',
        'prune-backups': {'keep-last': 3, 'keep-weekly': 4},
    }


@route('GET', r'/cluster/backup/([^/]+)/included_volumes')
def backup_included(m, query, body):
    return {
        'children': [
            {'id': 100, 'type': 'qemu', 'name': 'web-01', 'children': [
                {'id': 'local-lvm:vm-100-disk-0', 'name': 'scsi0', 'included': 1, 'reason': 'all'},
                {'id': 'local-lvm:vm-100-cloudinit', 'name': 'ide2', 'included': 0, 'reason': 'cdrom'},
            ]},
            {'id': 101, 'type': 'qemu', 'name': 'db-01', 'children': [
                {'id': 'local-lvm:vm-101-disk-0', 'name': 'scsi0', 'included': 1, 'reason': 'all'},
            ]},
            {'id': 200, 'type': 'lxc', 'name': 'dns', 'children': [
                {'id': 'local-lvm:subvol-200-disk-0', 'name': 'rootfs', 'included': 1, 'reason': 'all'},
            ]},
        ]
    }


@route('GET', r'/cluster/backup-info/not-backed-up')
def backup_not_backed_up(m, query, body):
    return [
        dict(vmid='102', name='win11-test', type='qemu'),
        dict(vmid='9000', name='debian-12-template', type='qemu'),
    ]


@route('GET', r'/cluster/jobs/schedule-analyze')
def backup_schedule(m, query, body):
    return [dict(timestamp=NOW + 3600 * i, utc='2026-11-05T%02d:00:00Z' % (i % 24)) for i in range(1, 6)]


@route('GET', r'/cluster/replication')
def replication_jobs(m, query, body):
    return [
        dict(id='100-0', guest='100', type='local', source='pve1', target='pve2', schedule='*/15',
             disable=0, comment='web-01 standby', rate=''),
        dict(id='101-0', guest='101', type='local', source='pve1', target='pve2', schedule='*/30',
             disable=1, comment='', rate='50'),
    ]


@route('GET', r'/cluster/options')
def cluster_options(m, query, body):
    return {
        'keyboard': 'en-us',
        'console': 'html5',
        'email_from': 'pve@lab.example',
        'mac_prefix': 'BC:24:11',
        'http_proxy': 'http://proxy.lab.example:3128',
        'migration': {'type': 'secure', 'network': '10.10.0.0/24'},
        'ha': {'shutdown_policy': 'migrate'},
        'crs': {'ha': 'basic', 'ha-rebalance-on-start': 1},
        'bwlimit': 'migration=102400,clone=51200',
        'max_workers': 4,
        'next-id': {'lower': 100, 'upper': 100000},
        'tag-style': {'shape': 'circle', 'ordering': 'config'},
        'user-tag-access': {'user-allow': 'list', 'user-allow-list': 'ops;dev'},
        'registered-tags': ['prod', 'web'],
        'webauthn': {'rp': 'PVE Lab', 'origin': 'https://pve1.lab.example:8006', 'id': 'pve1.lab.example'},
    }


@route('GET', r'/cluster/log')
def cluster_log(m, query, body):
    return [
        dict(uid='7', time=NOW - 120, node='pve1', user='root@pam', tag='pvedaemon', pri='6',
             msg='starting task UPID:pve1:0000A1B2:0012C3D4:qmstart:100:root@pam:'),
        dict(uid='6', time=NOW - 900, node='pve2', user='root@pam', tag='pvescheduler', pri='6',
             msg='backup job backup-db-daily started'),
        dict(uid='5', time=NOW - 3600, node='pve1', user='root@pam', tag='pmxcfs', pri='6',
             msg='received new cluster configuration'),
        dict(uid='4', time=NOW - 7200, node='pve2', user='root@pam', tag='corosync', pri='5',
             msg='Token has not been received in 4000 ms'),
    ]


@route('GET', r'/cluster/qemu/custom-cpu-models')
def cpu_models(m, query, body):
    return [
        dict(cputype='lab-epyc', **{'reported-model': 'EPYC-Rome'}, flags='+aes;-avx2', hidden=0,
             **{'phys-bits': 'host'}),
        dict(cputype='lab-xeon-legacy', **{'reported-model': 'Skylake-Server'}, flags='-pcid', hidden=1,
             **{'phys-bits': '40'}),
    ]


@route('GET', r'/nodes/([^/]+)/capabilities/qemu/cpu-flags')
def cpu_flags(m, query, body):
    return [
        dict(name='aes', description='Advanced Encryption Standard', **{'supported-on': 'x86-64'}),
        dict(name='avx2', description='Advanced Vector Extensions 2', **{'supported-on': 'x86-64'}),
        dict(name='pcid', description='Process-Context Identifiers', **{'supported-on': 'x86-64'}),
    ]

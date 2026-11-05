# 노드 관리 화면(요약·메모·네트워크·인증서·DNS·hosts·옵션·시간·서비스·시스템 로그·업데이트·방화벽·디스크·Ceph·SDN·
# 복제·작업·구독)의 예시 응답. route, STATE 는 fake_pve.py 가 넣어 준다. 값은 PVE 9 의 응답 모양을 따른다
# (숫자는 정수, 불리언은 1/0, 텍스트 응답은 문자열). 노드 이름은 pve1 · pve2.
import time as _time

_NOW = int(_time.time())
_DAY = 86400
_GIB = 1024 ** 3
_TIB = 1024 ** 4


def _osd(i, host, status, cls, size, used):
    return dict(type='osd', id=i, name=f'osd.{i}', host=host, status=status, device_class=cls,
                total_space=size, percent_used=f'{used:.1f}', ceph_version_short='19.2.3',
                **{'in': 0 if status == 'down' else 1})


_CEPH_OSDS = {
    'root': dict(type='root', name='default', children=[
        dict(type='host', name='pve1', children=[
            _osd(0, 'pve1', 'up', 'ssd', 960 * 1000 ** 3, 41.2),
            _osd(1, 'pve1', 'up', 'ssd', 960 * 1000 ** 3, 38.7),
        ]),
        dict(type='host', name='pve2', children=[
            _osd(2, 'pve2', 'up', 'hdd', 4 * 1000 ** 4, 12.5),
            _osd(3, 'pve2', 'down', 'hdd', 4 * 1000 ** 4, 0.0),
        ]),
    ]),
}


@route('GET', r'/nodes/([^/]+)/config')
def node_config(m, query, body):
    return dict(description='랩 노드 — 웹 서버 검증용', acme='account=default',
                acmedomain0='domain=pve1.example.com,plugin=cf', **{'startall-onboot-delay': '10'},
                wakeonlan='AA:BB:CC:DD:EE:01', digest='cfg-1')


@route('GET', r'/nodes/([^/]+)/dns')
def node_dns(m, query, body):
    return dict(search='example.local', dns1='192.168.10.1', dns2='1.1.1.1', dns3='')


@route('GET', r'/nodes/([^/]+)/time')
def node_time(m, query, body):
    return dict(timezone='Asia/Seoul', localtime=_NOW, time=_NOW, utctime='2026-11-05T01:00:00Z')


@route('GET', r'/nodes/([^/]+)/hosts')
def node_hosts(m, query, body):
    return dict(data='127.0.0.1 localhost\n192.168.10.11 pve1.local pve1\n192.168.10.12 pve2.local pve2\n',
                digest='hosts-1')


@route('GET', r'/nodes/([^/]+)/subscription')
def node_subscription(m, query, body):
    return dict(status='Active', productname='Proxmox VE Community Subscription 1 CPU/year',
                key='pve4b-1234567890', serverid='A1B2C3D4E5F6', sockets=1, level='c',
                regdate='2026-01-10 00:00:00', nextduedate='2027-01-10', checktime=_NOW,
                message='Your subscription is valid.', signature='SHOULD-NOT-SHOW')


@route('GET', r'/nodes/([^/]+)/certificates/info')
def node_certificates(m, query, body):
    return [
        dict(filename='pveproxy-ssl.pem', fingerprint='AB:CD:EF:01:23:45:67:89', issuer='CN=pve1.local',
             subject='CN=pve1.local', notafter=_NOW + 300 * _DAY, notbefore=_NOW - 30 * _DAY,
             san='pve1.local,192.168.10.11', public_key_type='rsaEncryption', public_key_bits=2048),
        dict(filename='pve-root-ca.pem', fingerprint='11:22:33:44:55:66:77:88', issuer='CN=Proxmox Virtual Environment',
             subject='CN=Proxmox Virtual Environment', notafter=_NOW + 3650 * _DAY, notbefore=_NOW - 365 * _DAY,
             san='', public_key_type='rsaEncryption', public_key_bits=4096),
    ]


@route('GET', r'/nodes/([^/]+)/services')
def node_services(m, query, body):
    return [
        dict(name='pveproxy', service='pveproxy', **{'active-state': 'active', 'unit-state': 'enabled'},
             desc='PVE API Proxy Server'),
        dict(name='pvedaemon', service='pvedaemon', **{'active-state': 'active', 'unit-state': 'enabled'},
             desc='PVE API Daemon'),
        dict(name='pve-cluster', service='pve-cluster', **{'active-state': 'active', 'unit-state': 'enabled'},
             desc='The Proxmox VE cluster filesystem'),
        dict(name='corosync', service='corosync', **{'active-state': 'active', 'unit-state': 'enabled'},
             desc='Corosync Cluster Engine'),
        dict(name='ceph-mon@pve1', service='ceph-mon@pve1', **{'active-state': 'inactive', 'unit-state': 'disabled'},
             desc='Ceph cluster monitor daemon'),
    ]


@route('GET', r'/nodes/([^/]+)/syslog')
def node_syslog(m, query, body):
    lines = [
        'Nov  5 00:58:01 pve1 pvedaemon[1234]: <root@pam> starting task UPID:pve1:00001234',
        'Nov  5 00:58:03 pve1 pvedaemon[1234]: <root@pam> end task UPID:pve1:00001234: OK',
        'Nov  5 00:59:10 pve1 pveproxy[2201]: worker 2202 started',
        'Nov  5 01:00:00 pve1 CRON[3301]: (root) CMD (/usr/sbin/pvesr)',
        'Nov  5 01:00:02 pve1 pvestatd[1011]: status update time (0.412 seconds)',
    ]
    return [dict(n=i + 1, t=t) for i, t in enumerate(lines)]


@route('GET', r'/nodes/([^/]+)/apt/update')
def node_apt_update(m, query, body):
    return [
        dict(Package='pve-manager', OldVersion='9.0.10', Version='9.0.11', Title='Proxmox VE management tools',
             Origin='Proxmox', Priority='optional'),
        dict(Package='qemu-server', OldVersion='9.0.2', Version='9.0.3', Title='Qemu Server tools', Origin='Proxmox',
             Priority='optional'),
        dict(Package='libc6', OldVersion='2.41-1', Version='2.41-2', Title='GNU C Library', Origin='Debian',
             Priority='important'),
    ]


@route('GET', r'/nodes/([^/]+)/apt/versions')
def node_apt_versions(m, query, body):
    return [
        dict(Package='pve-manager', Version='9.0.10', CurrentState='Installed', Title='Proxmox VE management tools'),
        dict(Package='proxmox-kernel-6.14', Version='6.14.8-2', CurrentState='Running', Title='Proxmox kernel'),
        dict(Package='ceph', Version='19.2.3-pve1', CurrentState='Installed', Title='Ceph distributed storage'),
    ]


@route('GET', r'/nodes/([^/]+)/apt/repositories')
def node_apt_repositories(m, query, body):
    return dict(digest='repo-1', files=[
        dict(path='/etc/apt/sources.list.d/pve-enterprise.sources', repositories=[
            dict(Enabled=1, Types='deb', URIs='https://enterprise.proxmox.com/debian/pve', Suites='trixie',
                 Components='pve-enterprise', Comment=''),
        ]),
        dict(path='/etc/apt/sources.list', repositories=[
            dict(Enabled=1, Types='deb', URIs='http://deb.debian.org/debian', Suites='trixie trixie-updates',
                 Components='main contrib', Comment=''),
        ]),
    ], **{'standard-repos': [
        dict(handle='no-subscription', name='PVE No-Subscription', status=0),
        dict(handle='enterprise', name='PVE Enterprise', status=1),
    ]})


@route('GET', r'/nodes/([^/]+)/apt/changelog')
def node_apt_changelog(m, query, body):
    return 'pve-manager (9.0.11) trixie; urgency=medium\n\n  * fix storage refresh\n  * update translations\n'


@route('GET', r'/nodes/([^/]+)/report')
def node_report(m, query, body):
    return '# Proxmox VE 시스템 보고서 (예시)\n\nkernel: 6.14.8-2-pve\nuptime: 12 days\n'


@route('GET', r'/nodes/([^/]+)/disks/list')
def node_disks(m, query, body):
    disks = [
        dict(devpath='/dev/sda', type='ssd', used='LVM', size=960 * 1000 ** 3, gpt=1, model='Samsung SSD 870',
             serial='S5Y1NX0R000001', health='PASSED', wearout=98),
        dict(devpath='/dev/sdb', type='hdd', used='', size=4 * 1000 ** 4, gpt=0, model='WDC WD40EFRX',
             serial='WD-WCC7K0000001', health='PASSED', wearout='N/A'),
        dict(devpath='/dev/nvme0n1', type='nvme', used='ZFS', size=1 * 1000 ** 4, gpt=1,
             model='Samsung 980 PRO', serial='S6B1NX0T000002', health='PASSED', wearout=97),
    ]
    if query.get('type') == 'unused':
        return [d for d in disks if not d['used']]
    return disks


@route('GET', r'/nodes/([^/]+)/disks/smart')
def node_smart(m, query, body):
    disk = query.get('disk', '')
    if disk.startswith('/dev/nvme'):
        return dict(health='PASSED', text='SMART/Health Information (NVMe Log 0x02)\nCritical Warning: 0x00\n')
    return dict(health='PASSED', type='ata', attributes=[
        dict(id=5, name='Reallocated_Sector_Ct', value=100, worst=100, threshold=10, raw='0', fail=''),
        dict(id=9, name='Power_On_Hours', value=92, worst=92, threshold=0, raw='21033', fail=''),
        dict(id=177, name='Wear_Leveling_Count', value=98, worst=98, threshold=0, raw='2', fail=''),
    ])


@route('GET', r'/nodes/([^/]+)/disks/lvm')
def node_lvm(m, query, body):
    return dict(leaves=[], children=[
        dict(name='pve', size=900 * _GIB, free=120 * _GIB, leaf=0),
    ])


@route('GET', r'/nodes/([^/]+)/disks/lvmthin')
def node_lvmthin(m, query, body):
    return [
        dict(lv='data', vg='pve', lv_size=800 * _GIB, used=310 * _GIB, metadata_size=8 * _GIB, lv_type='thin'),
    ]


@route('GET', r'/nodes/([^/]+)/disks/directory')
def node_directory(m, query, body):
    return [
        dict(path='/mnt/backup', device='/dev/sdb1', type='ext4', options='defaults', unitfile=''),
    ]


@route('GET', r'/nodes/([^/]+)/disks/zfs')
def node_zfs(m, query, body):
    return [
        dict(name='tank', size=1 * _TIB, free=700 * _GIB, alloc=324 * _GIB, frag=3, dedup='1.00', health='ONLINE'),
    ]


@route('GET', r'/nodes/([^/]+)/replication')
def node_replication(m, query, body):
    rows = [
        dict(id='100-0', guest=100, target='pve2', last_sync=_NOW - 900, next_sync=_NOW + 900, duration=12.4,
             fail_count=0, error=''),
        dict(id='101-0', guest=101, target='pve2', last_sync=_NOW - 7200, next_sync=_NOW + 1800, duration=35.1,
             fail_count=2, error='no route to host'),
    ]
    guest = query.get('guest')
    return [r for r in rows if guest is None or str(r['guest']) == str(guest)]


@route('GET', r'/nodes/([^/]+)/replication/([^/]+)/log')
def node_replication_log(m, query, body):
    return [dict(n=1, t=f'{m.group(2)}: 2026-11-05 00:45:00 start'),
            dict(n=2, t=f'{m.group(2)}: 2026-11-05 00:45:12 end')]


@route('GET', r'/nodes/([^/]+)/firewall/rules')
def node_fw_rules(m, query, body):
    return [
        dict(pos=0, type='in', action='ACCEPT', enable=1, proto='tcp', dport='8006', source='192.168.10.0/24',
             comment='웹 UI'),
        dict(pos=1, type='in', action='ACCEPT', enable=1, proto='tcp', dport='22', comment='SSH'),
        dict(pos=2, type='in', action='DROP', enable=0, proto='udp', dport='5405:5412', comment='corosync (비활성)'),
    ]


@route('GET', r'/nodes/([^/]+)/firewall/options')
def node_fw_options(m, query, body):
    return dict(enable=1, log_level_in='info', log_level_out='nolog', ndp=1, nosmurfs=1, tcpflags=0)


@route('GET', r'/nodes/([^/]+)/firewall/log')
def node_fw_log(m, query, body):
    return [dict(n=1, t='2026-11-05T00:50:01 ACCEPT tcp 192.168.10.5 -> 192.168.10.11:8006')]


@route('GET', r'/nodes/([^/]+)/ceph/status')
def node_ceph_status(m, query, body):
    return dict(health=dict(status='HEALTH_WARN', checks=dict(OSD_DOWN=dict(severity='HEALTH_WARN',
                                                                          summary=dict(message='1 osds down')))),
                monmap=dict(num_mons=1), osdmap=dict(num_osds=4, num_up_osds=3, num_in_osds=3),
                pgmap=dict(num_pgs=128, bytes_used=1_200_000_000_000, bytes_total=6_000_000_000_000))


@route('GET', r'/nodes/([^/]+)/ceph/(cfg/raw|config)')
def node_ceph_config(m, query, body):
    return '[global]\n\tauth_client_required = cephx\n\tpublic_network = 192.168.10.0/24\n\tosd_pool_default_size = 3\n'


@route('GET', r'/nodes/([^/]+)/ceph/cfg/db')
def node_ceph_cfg_db(m, query, body):
    return [
        dict(section='global', name='osd_memory_target', value='4294967296', level='advanced',
             can_update_at_runtime=1),
        dict(section='mon', name='mon_allow_pool_delete', value='true', level='advanced',
             can_update_at_runtime=1),
    ]


@route('GET', r'/nodes/([^/]+)/ceph/log')
def node_ceph_log(m, query, body):
    return [dict(n=1, t='2026-11-05 00:40:01.123 cluster [INF] mon.pve1 is new leader'),
            dict(n=2, t='2026-11-05 00:50:12.456 cluster [WRN] Health check failed: 1 osds down (OSD_DOWN)')]


@route('GET', r'/nodes/([^/]+)/ceph/(mon|mgr|mds)')
def node_ceph_services(m, query, body):
    kind = m.group(2)
    if kind == 'mon':
        return [dict(name='pve1', host='pve1', addr='192.168.10.11:6789', state='running',
                     ceph_version_short='19.2.3')]
    if kind == 'mgr':
        return [dict(name='pve1', host='pve1', addr='192.168.10.11', state='active', ceph_version_short='19.2.3')]
    return [dict(name='pve2', host='pve2', addr='192.168.10.12', state='running', ceph_version_short='19.2.3')]


@route('GET', r'/nodes/([^/]+)/ceph/osd')
def node_ceph_osd(m, query, body):
    return dict(root=_CEPH_OSDS['root'])


@route('GET', r'/nodes/([^/]+)/ceph/osd/([^/]+)/metadata')
def node_ceph_osd_meta(m, query, body):
    return dict(id=m.group(2), hostname='pve1', osd_objectstore='bluestore', bluestore_bdev_type='ssd',
                device_ids='sda=Samsung_SSD_870', osd_data='/var/lib/ceph/osd/ceph-' + m.group(2))


@route('GET', r'/nodes/([^/]+)/ceph/osd/([^/]+)/lv-info')
def node_ceph_osd_lv(m, query, body):
    return dict(lv_name=f'osd-block-{m.group(2)}', lv_path='/dev/ceph-1/osd-block-' + m.group(2),
                lv_size=960 * 1000 ** 3)


@route('GET', r'/nodes/([^/]+)/ceph/fs')
def node_ceph_fs(m, query, body):
    return [dict(name='cephfs', metadata_pool='cephfs_metadata', data_pool='cephfs_data')]


@route('GET', r'/nodes/([^/]+)/ceph/pool')
def node_ceph_pools(m, query, body):
    return [
        dict(pool_name='vm-pool', size=3, min_size=2, pg_num=64, pg_autoscale_mode='on',
             crush_rule_name='replicated_rule', bytes_used=420 * _GIB, target_size_ratio=0.5),
        dict(pool_name='device_health_metrics', size=3, min_size=2, pg_num=1, pg_autoscale_mode='on',
             crush_rule_name='replicated_rule', bytes_used=0),
    ]


@route('GET', r'/nodes/([^/]+)/ceph/pool/([^/]+)/status')
def node_ceph_pool_status(m, query, body):
    return dict(pool=m.group(2), size=3, min_size=2, pg_num=64, bytes_used=420 * _GIB, percent_used=0.21)


@route('GET', r'/nodes/([^/]+)/ceph/crush')
def node_ceph_crush(m, query, body):
    return '# begin crush map\ndevice 0 osd.0 class ssd\ndevice 1 osd.1 class ssd\n# end crush map\n'


@route('GET', r'/nodes/([^/]+)/ceph/releases')
def node_ceph_releases(m, query, body):
    return [
        dict(release='reef', version='18.2.7', available=1, **{'is-default': 0, 'unsupported': 0}),
        dict(release='squid', version='19.2.3', available=1, **{'is-default': 1, 'unsupported': 0}),
        dict(release='tentacle', version='20.1.0', available=0, **{'is-default': 0, 'unsupported': 1}),
    ]


@route('GET', r'/nodes/([^/]+)/ceph/cmd-safety')
def node_ceph_safety(m, query, body):
    return dict(safe=1, status='')


@route('GET', r'/cluster/ceph/flags')
def cluster_ceph_flags(m, query, body):
    return [
        dict(name='noout', value=0, description='OSD 가 내려가도 데이터를 다른 OSD 로 옮기지 않음'),
        dict(name='norebalance', value=0, description='재배치 중지'),
        dict(name='noscrub', value=1, description='정기 스크럽 중지'),
    ]


@route('GET', r'/cluster/ceph/health-mute')
def cluster_ceph_mutes(m, query, body):
    return [dict(code='OSD_DOWN', summary='1 osds down', ttl='2h', sticky=0)]


@route('GET', r'/cluster/ceph/metadata')
def cluster_ceph_metadata(m, query, body):
    return dict(mon=dict(pve1='19.2.3'), osd=dict(pve1='19.2.3', pve2='19.2.3'))


@route('GET', r'/nodes/([^/]+)/sdn/zones/([^/]+)/bridges')
def node_sdn_bridges(m, query, body):
    return [dict(name='vmbr1', ports='tap100i0,tap101i0', vlan_filtering=1)]


@route('GET', r'/nodes/([^/]+)/sdn/zones/([^/]+)/ip-vrf')
def node_sdn_ip_vrf(m, query, body):
    return [dict(ip='10.10.0.0/24', protocol='bgp', metric=20, nexthops='192.168.10.1')]


@route('GET', r'/nodes/([^/]+)/sdn/vnets/([^/]+)/mac-vrf')
def node_sdn_mac_vrf(m, query, body):
    return [dict(mac='BC:24:11:AA:BB:CC', ip='10.10.0.21', nexthop='192.168.10.12')]


@route('GET', r'/nodes/([^/]+)/sdn/fabrics/([^/]+)/routes')
def node_sdn_routes(m, query, body):
    return [dict(route='10.10.0.0/24', via='eth1')]


@route('GET', r'/nodes/([^/]+)/sdn/fabrics/([^/]+)/neighbors')
def node_sdn_neighbors(m, query, body):
    return [dict(neighbor='fe80::2', status='Established', uptime='12h 03m')]


@route('GET', r'/nodes/([^/]+)/sdn/fabrics/([^/]+)/interfaces')
def node_sdn_interfaces(m, query, body):
    return [dict(name='eth1', type='ethernet', state='up')]

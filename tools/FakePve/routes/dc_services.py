# 데이터센터 "서비스" 화면(HA·SDN·ACME·방화벽·지표·매핑·알림·클러스터 설정, 게스트 방화벽)의 예시 응답.
# route, STATE 는 fake_pve.py 가 넣어 준다. 값은 PVE 9 의 응답 모양을 따른다(숫자는 정수, 불리언은 1/0).

_SDN_STATUS = [
    dict(sdn='prod', node='pve1', status='ok'),
    dict(sdn='prod', node='pve2', status='ok'),
    dict(sdn='dev', node='pve1', status='pending'),
]


@route('GET', r'/cluster/config/nodes')
def config_nodes(m, query, body):
    return [
        dict(name='pve1', nodeid=1, quorum_votes=1, ring0_addr='192.168.10.11', ring1_addr=''),
        dict(name='pve2', nodeid=2, quorum_votes=1, ring0_addr='192.168.10.12', ring1_addr='10.10.0.12'),
    ]


# 클러스터 화면의 SDN 상태 표는 /cluster/resources?type=sdn 을 쓴다. 이 경로는 core 가 이미 처리하므로
# 여기서는 같은 규칙으로 되돌려 준다: type=sdn 이면 SDN 상태, 그 밖에는 core 와 같은 응답(vm·storage·전체).
@route('GET', r'/cluster/resources')
def cluster_resources(m, query, body):
    kind = query.get('type', '')
    if kind == 'sdn':
        return _SDN_STATUS
    if kind == 'vm':
        return STATE.GUESTS
    if kind == 'storage':
        return STATE.STOR
    return STATE.GUESTS + STATE.NODES + STATE.STOR


# ---------------------------------------------------------------- HA
@route('GET', r'/cluster/ha/resources')
def ha_resources(m, query, body):
    return [
        dict(sid='vm:100', type='vm', state='started', max_restart=1, max_relocate=1, comment='웹 서버'),
        dict(sid='ct:200', type='ct', state='started', max_restart=2, max_relocate=1, comment='DNS'),
        dict(sid='vm:101', type='vm', state='stopped', max_restart=1, max_relocate=1, comment='DB (점검 중)'),
    ]


@route('GET', r'/cluster/ha/status/current')
def ha_status(m, query, body):
    return [
        dict(id='quorum', type='quorum', node='', status='OK', quorate=1, request_state=''),
        dict(id='master', type='master', node='pve1', status='active', request_state=''),
        dict(id='lrm:pve1', type='lrm', node='pve1', status='active', request_state=''),
        dict(id='lrm:pve2', type='lrm', node='pve2', status='active', request_state=''),
        dict(id='service:vm:100', type='service', node='pve1', status='started', request_state='started'),
        dict(id='service:ct:200', type='service', node='pve2', status='started', request_state='started'),
        dict(id='service:vm:101', type='service', node='pve1', status='stopped', request_state='stopped'),
    ]


@route('GET', r'/cluster/ha/rules')
def ha_rules(m, query, body):
    return [
        dict(rule='pin-web', type='node-affinity', resources='vm:100', nodes='pve1:2,pve2:1', strict=1,
             disable=0, comment='웹 서버는 pve1 선호'),
        dict(rule='apart-db', type='resource-affinity', resources='vm:100,vm:101', affinity='negative',
             disable=0, comment='웹과 DB 분리'),
    ]


# ---------------------------------------------------------------- SDN
@route('GET', r'/cluster/sdn/zones')
def sdn_zones(m, query, body):
    return [
        dict(zone='prod', type='vlan', bridge='vmbr0', nodes='pve1,pve2', mtu=1500, state='ok'),
        dict(zone='dev', type='simple', bridge='', nodes='', mtu='', state='pending'),
    ]


@route('GET', r'/cluster/sdn/vnets')
def sdn_vnets(m, query, body):
    return [
        dict(vnet='vnet100', zone='prod', tag=100, alias='web', vlanaware=1, state='ok'),
        dict(vnet='vnet200', zone='prod', tag=200, alias='db', vlanaware=0, state='ok'),
        dict(vnet='devnet', zone='dev', tag='', alias='개발', vlanaware=0, state='pending'),
    ]


@route('GET', r'/cluster/sdn/vnets/([^/]+)/subnets')
def sdn_subnets(m, query, body):
    return [dict(subnet='prod-10.10.100.0-24', cidr='10.10.100.0/24', gateway='10.10.100.1', snat=1, state='ok')]


@route('GET', r'/cluster/sdn/controllers')
def sdn_controllers(m, query, body):
    return [dict(controller='evpn1', type='evpn', asn=65000, peers='10.0.0.1,10.0.0.2', node='pve1', state='ok')]


@route('GET', r'/cluster/sdn/ipams')
def sdn_ipams(m, query, body):
    return [dict(ipam='pve', type='pve'), dict(ipam='netbox', type='netbox', url='https://netbox.example.lan/api')]


@route('GET', r'/cluster/sdn/dns')
def sdn_dns(m, query, body):
    return [dict(dns='cloudflare', type='powerdns', url='https://dns.example.lan/api', ttl=3600)]


@route('GET', r'/cluster/sdn/fabrics/fabric')
def sdn_fabrics(m, query, body):
    return [dict(id='fab1', protocol='openfabric', ip_prefix='10.100.0.0/24', ip6_prefix='', area='49.0001')]


@route('GET', r'/cluster/sdn/prefix-lists')
def sdn_prefix_lists(m, query, body):
    return [dict(id='pl-internal', state='ok')]


@route('GET', r'/cluster/sdn/prefix-lists/([^/]+)/entries')
def sdn_prefix_entries(m, query, body):
    return [dict(seq=10, action='permit', prefix='10.0.0.0/8', ge=16, le=24)]


@route('GET', r'/cluster/sdn/route-maps/entries')
def sdn_route_maps(m, query, body):
    return [dict(**{'route-map-id': 'rm-out', 'order': 10, 'action': 'permit', 'match': 'ip address prefix-list pl-internal', 'set': ''})]


# ---------------------------------------------------------------- 방화벽(데이터센터·게스트)
_RULES = [
    dict(pos=0, enable=1, type='in', action='ACCEPT', macro='SSH', iface='', proto='tcp', source='10.0.0.0/8',
         sport='', dest='', dport='22', log='nolog', comment='SSH 내부망'),
    dict(pos=1, enable=1, type='in', action='ACCEPT', macro='', iface='', proto='tcp', source='', sport='',
         dest='', dport='8006', log='nolog', comment='웹 UI'),
    dict(pos=2, enable=0, type='in', action='DROP', macro='', iface='', proto='udp', source='', sport='',
         dest='', dport='1900', log='info', comment='SSDP 차단(꺼짐)'),
]


@route('GET', r'/cluster/firewall/rules')
def fw_cluster_rules(m, query, body):
    return _RULES


@route('GET', r'/nodes/([^/]+)/(qemu|lxc)/(\d+)/firewall/rules')
def fw_guest_rules(m, query, body):
    # 실서버처럼 행마다 규칙 파일의 digest 를 붙인다(routes/manage.py 가 바꾸고 검사한다)
    digest = getattr(STATE, 'FW_GUEST_DIGEST', 'fwdigest1')
    return [
        dict(pos=0, enable=1, type='in', action='ACCEPT', macro='SSH', iface='', proto='tcp', source='',
             sport='', dest='', dport='22', log='nolog', comment='관리자 SSH', digest=digest),
        dict(pos=1, enable=1, type='in', action='ACCEPT', macro='HTTPS', iface='', proto='tcp', source='',
             sport='', dest='', dport='443', log='nolog', comment='웹 서비스', digest=digest),
    ]


@route('GET', r'/cluster/firewall/groups')
def fw_groups(m, query, body):
    return [dict(group='web', comment='웹 서버 규칙'), dict(group='monitoring', comment='모니터링 에이전트')]


@route('GET', r'/cluster/firewall/aliases')
def fw_aliases(m, query, body):
    return [
        dict(name='internal', cidr='10.0.0.0/8', comment='사내망'),
        dict(name='mgmt', cidr='192.168.10.0/24', comment='관리 대역'),
    ]


@route('GET', r'/cluster/firewall/ipset')
def fw_ipsets(m, query, body):
    return [dict(name='trusted', comment='신뢰하는 주소'), dict(name='blocked', comment='차단 목록')]


@route('GET', r'/cluster/firewall/ipset/([^/]+)')
def fw_ipset_entries(m, query, body):
    name = m.group(1)
    if name == 'blocked':
        return [dict(cidr='198.51.100.0/24', nomatch=0, comment='스캔 대역')]
    return [dict(cidr='203.0.113.0/24', nomatch=0, comment='사무실'), dict(cidr='203.0.113.99', nomatch=1, comment='제외')]


@route('GET', r'/cluster/firewall/options')
def fw_cluster_options(m, query, body):
    return dict(enable=1, policy_in='DROP', policy_out='ACCEPT', policy_forward='ACCEPT', ebtables=1,
                log_ratelimit='enable=1,per_second=50,burst=100')


@route('GET', r'/nodes/([^/]+)/(qemu|lxc)/(\d+)/firewall/options')
def fw_guest_options(m, query, body):
    return dict(enable=1, policy_in='DROP', policy_out='ACCEPT', dhcp=1, ndp=1, radv=0, macfilter=1, ipfilter=0,
                log_level_in='info', log_level_out='nolog')


# ---------------------------------------------------------------- ACME
@route('GET', r'/cluster/acme/account')
def acme_accounts(m, query, body):
    return [dict(name='default')]


@route('GET', r'/cluster/acme/account/([^/]+)')
def acme_account(m, query, body):
    return dict(name=m.group(1), directory='https://acme-v02.api.letsencrypt.org/directory',
                contact='mailto:admin@example.com', tos='https://letsencrypt.org/documents/LE-SA-v1.5.pdf')


@route('GET', r'/cluster/acme/directories')
def acme_directories(m, query, body):
    return [
        dict(name="Let's Encrypt V2", url='https://acme-v02.api.letsencrypt.org/directory'),
        dict(name="Let's Encrypt V2 (Staging)", url='https://acme-staging-v02.api.letsencrypt.org/directory'),
    ]


@route('GET', r'/cluster/acme/plugins')
def acme_plugins(m, query, body):
    return [
        dict(plugin='cf-dns', type='dns', api='cf', **{'validation-delay': 30}, nodes='pve1', disable=0),
        dict(plugin='standalone', type='standalone', api='', **{'validation-delay': ''}, nodes='', disable=1),
    ]


@route('GET', r'/cluster/acme/tos')
def acme_tos(m, query, body):
    return 'https://letsencrypt.org/documents/LE-SA-v1.5.pdf'


# ---------------------------------------------------------------- 지표 서버·매핑·알림
@route('GET', r'/cluster/metrics/server')
def metric_servers(m, query, body):
    return [
        dict(id='influx1', type='influxdb', server='10.0.0.5', port=8086, disable=0),
        dict(id='graphite1', type='graphite', server='10.0.0.6', port=2003, disable=1),
    ]


@route('GET', r'/cluster/mapping/pci')
def mapping_pci(m, query, body):
    return [dict(id='gpu-rtx', map='node=pve1,path=0000:01:00.0,id=10de:2684,iommugroup=1',
                 description='RTX 4090')]


@route('GET', r'/cluster/mapping/usb')
def mapping_usb(m, query, body):
    return [dict(id='usb-yubikey', map='node=pve2,id=1050:0407', description='YubiKey')]


@route('GET', r'/cluster/mapping/dir')
def mapping_dir(m, query, body):
    return [dict(id='share-nas', map='node=pve1,path=/mnt/nas', description='NAS 공유 폴더')]


@route('GET', r'/cluster/notifications/targets')
def notify_targets(m, query, body):
    return [
        dict(name='mail-admin', type='sendmail', origin='user-created', disable=0, comment='관리자 메일'),
        dict(name='gotify', type='gotify', origin='user-created', disable=1, comment='푸시 알림(꺼짐)'),
    ]


@route('GET', r'/cluster/notifications/matchers')
def notify_matchers(m, query, body):
    return [
        dict(name='default-matcher', mode='all', target='mail-admin', **{'match-severity': 'warning'},
             disable=0, comment='경고 이상'),
    ]

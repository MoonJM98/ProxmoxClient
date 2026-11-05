# VM·CT 만들기 마법사가 부르는 API 의 예시 응답.
# 다음 VMID · 노드 브리지(any_bridge) · 저장소 내용(ISO·CT 템플릿). 같은 경로를 guest_hardware.py 도 쓰므로
# (routes 는 파일 이름 순으로 등록된다 — create.py 가 먼저) 하드웨어 화면이 쓰는 응답과 같은 값을 그대로 둔다.

@route('GET', r'/cluster/nextid')
def next_vmid(m, query, body):
    return 101


_ALL_NETWORK = [
    {'iface': 'vmbr0', 'type': 'bridge', 'active': 1, 'address': '192.168.10.10/24'},
    {'iface': 'vmbr1', 'type': 'bridge', 'active': 1, 'bridge_ports': 'eno2'},
    {'iface': 'eno1', 'type': 'eth', 'active': 1},
]


@route('GET', r'/nodes/([^/]+)/network')
def node_network(m, query, body):
    # 마법사는 type=any_bridge 로 브리지만 묻는다 — 그 밖의 요청(하드웨어 화면)은 전체 목록을 준다
    if query.get('type') == 'any_bridge':
        return [row for row in _ALL_NETWORK if row['type'] == 'bridge']
    return list(_ALL_NETWORK)


_CT_TEMPLATES = {
    'local': [
        {'volid': 'local:vztmpl/debian-12-standard_12.12-1_amd64.tar.zst', 'content': 'vztmpl',
         'format': 'tzst', 'size': 131_072_000},
        {'volid': 'local:vztmpl/ubuntu-24.04-standard_24.04-2_amd64.tar.zst', 'content': 'vztmpl',
         'format': 'tzst', 'size': 142_606_336},
    ],
}

_ISO = {
    'local': [
        {'volid': 'local:iso/debian-12.12.0-amd64-netinst.iso', 'content': 'iso', 'format': 'iso',
         'size': 661651456},
        {'volid': 'local:iso/ubuntu-24.04.2-live-server-amd64.iso', 'content': 'iso', 'format': 'iso',
         'size': 3218231296},
    ],
    'nas': [
        {'volid': 'nas:iso/virtio-win-0.1.262.iso', 'content': 'iso', 'format': 'iso', 'size': 528482304},
    ],
}


@route('GET', r'/nodes/([^/]+)/storage/([^/]+)/content')
def storage_content(m, query, body):
    storage, want = m.group(2), query.get('content', '')
    if want == 'vztmpl':
        return list(_CT_TEMPLATES.get(storage, []))
    if want == 'iso':
        return list(_ISO.get(storage, []))
    return []

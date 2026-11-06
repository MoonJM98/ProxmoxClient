# 게스트 하드웨어 화면(VM 100 · CT 200)이 부르는 API 의 예시 응답.
# 설정(config)·대기 중 변경(pending)·저장소·브리지·CPU/머신 목록·PCI/USB 장치·매핑·mdev 를 둔다.

VM_CONFIG = {
    'name': 'web-01', 'ostype': 'l26', 'machine': 'q35', 'bios': 'ovmf', 'arch': 'x86_64',
    'cores': '4', 'sockets': '1', 'memory': '4096', 'balloon': '2048', 'shares': '1000',
    'cpu': 'x86-64-v2-AES', 'numa': '0', 'vga': 'qxl', 'scsihw': 'virtio-scsi-single',
    'boot': 'order=scsi0;ide2;net0', 'agent': '1', 'hotplug': 'network,disk,usb',
    'meta': 'creation-qemu=9.2.0,ctime=1735000000',
    'efidisk0': 'local-lvm:vm-100-disk-1,efitype=4m,pre-enrolled-keys=1,size=528K',
    'tpmstate0': 'local-lvm:vm-100-disk-2,size=4M,version=v2.0',
    'scsi0': 'local-lvm:vm-100-disk-0,discard=on,iothread=1,size=32G,ssd=1',
    'ide2': 'local:iso/debian-12.12.0-amd64-netinst.iso,media=cdrom',
    'net0': 'virtio=BC:24:11:AA:BB:CC,bridge=vmbr0,firewall=1,tag=10',
    'usb0': 'host=1234:5678,usb3=1',
    'hostpci0': '0000:01:00,pcie=1,x-vga=1',
    'serial0': 'socket',
    'audio0': 'device=ich9-intel-hda,driver=spice',
    'rng0': 'source=/dev/urandom,max_bytes=1024,period=1000',
    'virtiofs0': 'share,cache=auto,direct-io=1',
    'unused0': 'local-lvm:vm-100-disk-3',
    'digest': 'abc',
}

CT_CONFIG = {
    'hostname': 'dns', 'ostype': 'debian', 'arch': 'amd64', 'unprivileged': '1',
    'cores': '2', 'cpuunits': '1024', 'cpulimit': '1.5', 'memory': '512', 'swap': '256',
    'rootfs': 'local-lvm:vm-200-disk-0,size=8G',
    'mp0': 'nas:200/vm-200-disk-1.raw,mp=/mnt/data,backup=1,size=8G',
    'mp1': 'local-lvm:vm-200-disk-2,mp=/srv/logs,size=4G,mountoptions=noatime;nodev,ro=1',
    'net0': 'name=eth0,bridge=vmbr0,hwaddr=BC:24:11:12:34:56,ip=192.168.10.53/24,gw=192.168.10.1,firewall=1,tag=20',
    'dev0': '/dev/ttyUSB0,gid=20,mode=0660',
    'features': 'nesting=1',
    'unused0': 'local-lvm:vm-200-disk-9',
    'digest': 'def',
}

# 대기 중 변경: 메모리(재시작 후 적용), 디스크 한 줄 삭제 예정(delete), CT 코어 대기
VM_PENDING = {'memory': '8192', 'hotplug': 'network,disk,usb'}
CT_PENDING = {'memory': '1024'}


def _pending_rows(config, pending):
    rows = []
    for key, value in config.items():
        row = {'key': key, 'value': value}
        if key in pending:
            row['pending'] = pending[key]
        rows.append(row)
    # 재시작 후 지워질 항목 — 웹 UI 의 delete=1 표시
    rows.append({'key': 'serial0', 'value': 'socket', 'delete': 1})
    return rows


def _config_for(vmid):
    if vmid == 100:
        return VM_CONFIG
    if vmid == 200:
        return CT_CONFIG
    return {}


@route('GET', r'/nodes/([^/]+)/(qemu|lxc)/(\d+)/config')
def guest_config(m, query, body):
    return dict(_config_for(int(m.group(3))))


@route('PUT', r'/nodes/([^/]+)/(qemu|lxc)/(\d+)/config')
def guest_config_put(m, query, body):
    # 실서버처럼 보낸 digest 가 지금 설정과 다르면 거절한다(PVE::Tools::assert_if_modified)
    current = _config_for(int(m.group(3))).get('digest')
    if body.get('digest') and current and body['digest'] != current:
        raise STATE.PveError(500, 'detected modified configuration - file changed by other user? Try again.\n')
    return None


@route('POST', r'/nodes/([^/]+)/(qemu|lxc)/(\d+)/config')
def guest_config_post(m, query, body):
    return None


@route('GET', r'/nodes/([^/]+)/(qemu|lxc)/(\d+)/pending')
def guest_pending(m, query, body):
    vmid = int(m.group(3))
    if vmid == 100:
        return _pending_rows(VM_CONFIG, VM_PENDING)
    if vmid == 200:
        return _pending_rows(CT_CONFIG, CT_PENDING)
    return []


@route('GET', r'/nodes/([^/]+)/network')
def node_bridges(m, query, body):
    return [
        {'iface': 'vmbr0', 'type': 'bridge', 'active': 1, 'address': '192.168.10.10/24'},
        {'iface': 'vmbr1', 'type': 'bridge', 'active': 1, 'bridge_ports': 'eno2'},
        {'iface': 'eno1', 'type': 'eth', 'active': 1},
    ]


_STORAGES = [
    {'storage': 'local', 'type': 'dir', 'content': 'iso,vztmpl,backup', 'enabled': 1, 'active': 1, 'shared': 0},
    {'storage': 'local-lvm', 'type': 'lvmthin', 'content': 'images,rootdir', 'enabled': 1, 'active': 1, 'shared': 0},
    {'storage': 'nas', 'type': 'nfs', 'content': 'backup,iso', 'enabled': 1, 'active': 1, 'shared': 1},
]


@route('GET', r'/nodes/([^/]+)/storage')
def node_storages(m, query, body):
    want = query.get('content')
    if not want:
        return list(_STORAGES)
    return [s for s in _STORAGES if want in s['content'].split(',')]


@route('GET', r'/nodes/([^/]+)/storage/([^/]+)/content')
def storage_content(m, query, body):
    storage, want = m.group(2), query.get('content', '')
    if want == 'iso' and storage == 'local':
        return [
            {'volid': 'local:iso/debian-12.12.0-amd64-netinst.iso', 'content': 'iso', 'format': 'iso',
             'size': 661651456},
            {'volid': 'local:iso/ubuntu-24.04.2-live-server-amd64.iso', 'content': 'iso', 'format': 'iso',
             'size': 3218231296},
        ]
    if want == 'iso' and storage == 'nas':
        return [{'volid': 'nas:iso/virtio-win-0.1.262.iso', 'content': 'iso', 'format': 'iso', 'size': 528482304}]
    return []


@route('GET', r'/nodes/([^/]+)/capabilities/qemu/cpu')
def cpu_models(m, query, body):
    return [
        {'name': 'x86-64-v2-AES', 'vendor': 'AuthenticAMD', 'custom': 0},
        {'name': 'EPYC', 'vendor': 'AuthenticAMD', 'custom': 0},
        {'name': 'Haswell', 'vendor': 'GenuineIntel', 'custom': 0},
        {'name': 'kvm64', 'vendor': 'default', 'custom': 0},
        {'name': 'qemu64', 'vendor': 'default', 'custom': 0},
        {'name': 'host', 'vendor': 'default', 'custom': 0},
    ]


@route('GET', r'/nodes/([^/]+)/capabilities/qemu/machines')
def machines(m, query, body):
    return [
        {'id': 'pc-q35-10.1', 'type': 'q35', 'version': '10.1'},
        {'id': 'pc-q35-9.2', 'type': 'q35', 'version': '9.2'},
        {'id': 'pc-i440fx-10.1', 'type': 'i440fx', 'version': '10.1'},
        {'id': 'pc-i440fx-9.2', 'type': 'i440fx', 'version': '9.2'},
    ]


@route('GET', r'/nodes/([^/]+)/hardware/pci')
def host_pci(m, query, body):
    return [
        {'id': '0000:01:00.0', 'vendor': '10de', 'device': '1c82', 'vendor_name': 'NVIDIA Corporation',
         'device_name': 'GP107 [GeForce GTX 1050 Ti]', 'class': '0300', 'iommugroup': 16},
        {'id': '0000:01:00.1', 'vendor': '10de', 'device': '0fb9', 'vendor_name': 'NVIDIA Corporation',
         'device_name': 'GP107GL High Definition Audio', 'class': '0403', 'iommugroup': 16},
        {'id': '0000:00:1f.3', 'vendor': '8086', 'device': 'a348', 'vendor_name': 'Intel Corporation',
         'device_name': 'Cannon Lake PCH HD Audio', 'class': '0403', 'iommugroup': 7},
    ]


@route('GET', r'/nodes/([^/]+)/hardware/usb')
def host_usb(m, query, body):
    return [
        {'usbpath': '1-1', 'vendid': '1d6b', 'prodid': '0002', 'manufacturer': 'Linux Foundation',
         'product': '2.0 root hub', 'class': '9'},
        {'usbpath': '1-2', 'vendid': '046d', 'prodid': 'c52b', 'manufacturer': 'Logitech',
         'product': 'Unifying Receiver', 'class': '0'},
    ]


@route('GET', r'/nodes/([^/]+)/hardware/pci/([^/]+)/mdev')
def pci_mdev(m, query, body):
    return [
        {'type': 'nvidia-63', 'available': 2, 'description': 'GRID GP107-1Q', 'name': 'GRID GP107-1Q'},
        {'type': 'nvidia-64', 'available': 1, 'description': 'GRID GP107-2Q', 'name': 'GRID GP107-2Q'},
    ]


@route('GET', r'/cluster/mapping/(pci|usb|dir)')
def cluster_mappings(m, query, body):
    kind = m.group(1)
    if kind == 'pci':
        return [{'id': 'gpu1', 'map': 'node=pve1,path=0000:01:00.0,id=10de:1c82'}]
    if kind == 'usb':
        return [{'id': 'yubikey', 'map': 'node=pve1,id=1050:0407'}]
    return [{'id': 'share', 'map': 'node=pve1,path=/srv/share'}]

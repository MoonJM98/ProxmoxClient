# 게스트 파일 화면의 예시 응답 — 게스트 에이전트(VM 100, Linux) 경로.
# 에이전트 명령(exec)은 목록 출력만 돌려준다(어떤 명령이든 같은 목록): 가짜 서버는 반복 인자를 첫 값만 받으므로
# 명령을 구분할 수 없다. 쓰기(file-write)·지우기·만들기는 성공으로 응답한다(목록은 다시 읽으면 그대로 보인다).
# CT(노드 셸) 경로는 termproxy 웹소켓(셸 대화)이 필요해 여기서 흉내 내지 않는다 — 화면은 연결 실패 안내를 보인다.
import base64
import itertools

_pids = itertools.count(4001)

# (이름, 종류 글자(d·f·l·ld), 크기, 수정(유닉스 초), 권한, 소유자, 그룹, 링크 대상)
_ENTRIES = [
    ('bin', 'ld', 7, 1750000000, '777', 'root', 'root', 'usr/bin'),
    ('boot', 'd', 4096, 1750000000, '755', 'root', 'root', ''),
    ('etc', 'd', 4096, 1761000000, '755', 'root', 'root', ''),
    ('home', 'd', 4096, 1761000000, '755', 'root', 'root', ''),
    ('lib', 'l', 7, 1750000000, '777', 'root', 'root', 'usr/lib'),
    ('root', 'd', 4096, 1761000000, '700', 'root', 'root', ''),
    ('srv', 'd', 4096, 1750000000, '755', 'root', 'root', ''),
    ('tmp', 'd', 4096, 1762000000, '1777', 'root', 'root', ''),
    ('usr', 'd', 4096, 1750000000, '755', 'root', 'root', ''),
    ('var', 'd', 4096, 1761000000, '755', 'root', 'root', ''),
    ('.bashrc', 'f', 3771, 1750000000, '644', 'root', 'root', ''),
    ('.profile', 'f', 807, 1750000000, '644', 'root', 'root', ''),
    ('README.txt', 'f', 2048, 1761500000, '644', 'root', 'root', ''),
    ('notes.md', 'f', 15342, 1762100000, '644', 'user', 'user', ''),
    ('backup-2026.tar.gz', 'f', 52428800, 1761200000, '640', 'root', 'root', ''),
    ('report.pdf', 'f', 1048576, 1762000000, '644', 'user', 'user', ''),
]


def _listing_base64():
    # 목록 레코드: "종류/크기/수정/권한/소유자/그룹" \0 이름 \0 링크 대상 \0 (항목마다)
    out = bytearray()
    for name, kind, size, mtime, mode, owner, group, link in _ENTRIES:
        out += '{}/{}/{}.0000000000/{}/{}/{}'.format(kind, size, mtime, mode, owner, group).encode() + b'\0'
        out += name.encode() + b'\0'
        out += link.encode() + b'\0'
    return base64.b64encode(bytes(out)).decode()


@route('GET', r'/nodes/([^/]+)/qemu/(\d+)/agent/info')
def agent_info(m, query, body):
    names = ['guest-exec', 'guest-exec-status', 'guest-file-read', 'guest-file-write', 'guest-get-osinfo',
             'guest-get-host-name', 'guest-network-get-interfaces', 'guest-get-fsinfo', 'guest-ping', 'guest-info']
    return {'result': {'supported_commands': [{'name': n, 'enabled': 1, 'success-response': 1} for n in names]}}


@route('GET', r'/nodes/([^/]+)/qemu/(\d+)/agent/get-osinfo')
def agent_osinfo(m, query, body):
    return {'result': {'id': 'debian', 'name': 'Debian GNU/Linux', 'pretty-name': 'Debian GNU/Linux 12 (bookworm)',
                       'version': '12 (bookworm)', 'version-id': '12', 'kernel-release': '6.1.0-18-amd64',
                       'machine': 'x86_64'}}


@route('GET', r'/nodes/([^/]+)/qemu/(\d+)/agent/get-fsinfo')
def agent_fsinfo(m, query, body):
    return {'result': [{'name': 'vda1', 'mountpoint': '/', 'type': 'ext4', 'total-bytes': 34359738368,
                        'used-bytes': 12884901888}]}


@route('POST', r'/nodes/([^/]+)/qemu/(\d+)/agent/exec')
def agent_exec(m, query, body):
    return {'pid': next(_pids)}


@route('GET', r'/nodes/([^/]+)/qemu/(\d+)/agent/exec-status')
def agent_exec_status(m, query, body):
    return {'exited': 1, 'exitcode': 0, 'out-data': _listing_base64(), 'err-data': '', 'out-truncated': 0,
            'err-truncated': 0}


@route('GET', r'/nodes/([^/]+)/qemu/(\d+)/agent/file-read')
def agent_file_read(m, query, body):
    path = query.get('file', '')
    name = path.rsplit('/', 1)[-1] or 'file'
    text = 'Sample content of {} (fake guest file).\nLine 2 for the viewer.\n'.format(name)
    return {'content': text, 'truncated': 0}


@route('POST', r'/nodes/([^/]+)/qemu/(\d+)/agent/file-write')
def agent_file_write(m, query, body):
    return {}

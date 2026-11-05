# 데이터센터 → 접근 제어 화면(사용자·2단계 인증·그룹·풀·역할·인증 영역·권한)의 예시 응답
import time

_NOW = int(time.time())
_DAY = 86400


@route('GET', r'/access/users')
def users(m, query, body):
    # full=1 이면 그룹·만료일까지 — 목록 칸과 같은 모양(PVE 응답은 문자열 값)
    return [
        {'userid': 'root@pam', 'enable': 1, 'expire': 0, 'firstname': '', 'lastname': '', 'email': 'admin@example.com',
         'groups': '', 'comment': '내장 관리자'},
        {'userid': 'alice@pve', 'enable': 1, 'expire': 0, 'firstname': 'Alice', 'lastname': 'Kim',
         'email': 'alice@example.com', 'groups': 'admins,devs', 'comment': '운영 담당'},
        {'userid': 'bob@pve', 'enable': 0, 'expire': _NOW + 90 * _DAY, 'firstname': 'Bob', 'lastname': 'Lee',
         'email': 'bob@example.com', 'groups': 'devs', 'comment': '계약직(휴면)'},
        {'userid': 'backup@pve', 'enable': 1, 'expire': 0, 'firstname': '', 'lastname': '',
         'email': '', 'groups': 'auditors', 'comment': '백업 스크립트 전용'},
    ]


@route('GET', r'/access/users/([^/]+)')
def user(m, query, body):
    return {'userid': m.group(1), 'enable': 1, 'expire': 0, 'groups': 'admins', 'comment': ''}


@route('GET', r'/access/groups')
def groups(m, query, body):
    return [
        {'groupid': 'admins', 'users': 'root@pam,alice@pve', 'comment': '시스템 관리자'},
        {'groupid': 'devs', 'users': 'alice@pve,bob@pve', 'comment': '개발팀'},
        {'groupid': 'auditors', 'users': 'backup@pve', 'comment': '감사·조회 전용'},
    ]


@route('GET', r'/access/roles')
def roles(m, query, body):
    return [
        {'roleid': 'Administrator', 'special': 1,
         'privs': 'Datastore.Allocate Datastore.Audit Pool.Allocate Realm.Allocate Sys.Audit Sys.Modify VM.Allocate '
                  'VM.Audit VM.Console VM.PowerMgmt'},
        {'roleid': 'PVEAuditor', 'special': 1, 'privs': 'Datastore.Audit Sys.Audit VM.Audit'},
        {'roleid': 'NoAccess', 'special': 1, 'privs': ''},
        {'roleid': 'Operator', 'special': 0, 'privs': 'VM.Audit VM.Console VM.PowerMgmt'},
    ]


@route('GET', r'/access/roles/([^/]+)')
def role(m, query, body):
    names = ['Datastore.Allocate', 'Datastore.Audit', 'Pool.Allocate', 'Realm.Allocate', 'Sys.Audit', 'Sys.Modify',
             'VM.Allocate', 'VM.Audit', 'VM.Console', 'VM.PowerMgmt']
    return {n: 1 for n in names}


@route('GET', r'/access/acl')
def acl(m, query, body):
    return [
        {'path': '/', 'type': 'user', 'ugid': 'root@pam', 'roleid': 'Administrator', 'propagate': 1},
        {'path': '/vms/100', 'type': 'group', 'ugid': 'devs', 'roleid': 'PVEVMUser', 'propagate': 0},
        {'path': '/pool/prod', 'type': 'user', 'ugid': 'alice@pve', 'roleid': 'PVEPoolUser', 'propagate': 1},
        {'path': '/storage/local', 'type': 'token', 'ugid': 'alice@pve!ci', 'roleid': 'PVEDatastoreUser',
         'propagate': 1},
        {'path': '/', 'type': 'group', 'ugid': 'auditors', 'roleid': 'PVEAuditor', 'propagate': 1},
    ]


@route('GET', r'/access/domains')
def domains(m, query, body):
    return [
        {'realm': 'pam', 'type': 'pam', 'comment': 'Linux PAM 표준 인증'},
        {'realm': 'pve', 'type': 'pve', 'comment': 'Proxmox VE 인증 서버'},
        {'realm': 'corp-ldap', 'type': 'ldap', 'comment': '사내 LDAP', 'tfa': 'type=oath,step=30,digits=6'},
        {'realm': 'corp-ad', 'type': 'ad', 'comment': '사내 Active Directory'},
        {'realm': 'sso', 'type': 'openid', 'comment': 'OpenID Connect(사내 SSO)'},
    ]


@route('GET', r'/access/domains/([^/]+)')
def domain(m, query, body):
    return {'realm': m.group(1), 'type': 'ldap', 'base_dn': 'ou=people,dc=example,dc=com', 'user_attr': 'uid',
            'server1': 'ldap1.example.com', 'port': 636, 'mode': 'ldaps', 'comment': '사내 LDAP',
            'sync-defaults-options': 'scope=both,enable-new=1,remove-vanished=acl;entry'}


@route('GET', r'/cluster/jobs/realm-sync')
def realm_sync_jobs(m, query, body):
    return [
        {'id': 'corp-ldap-nightly', 'realm': 'corp-ldap', 'enabled': 1, 'schedule': 'daily 02:00',
         'next-run': _NOW + 6 * 3600, 'scope': 'both', 'comment': '야간 동기화'},
        {'id': 'corp-ad-weekly', 'realm': 'corp-ad', 'enabled': 0, 'schedule': 'sat 01:00',
         'next-run': _NOW + 4 * _DAY, 'scope': 'groups', 'comment': '주간(중지)'},
    ]


@route('GET', r'/access/tfa')
def tfa(m, query, body):
    # PVE 응답은 사용자마다 항목(entries) 묶음 — 화면은 항목 한 줄씩 보인다
    return [
        {'userid': 'alice@pve', 'entries': [
            {'id': 'a1b2c3d4', 'type': 'totp', 'description': '휴대폰 인증 앱', 'enable': 1,
             'created': _NOW - 40 * _DAY},
            {'id': 'e5f6a7b8', 'type': 'recovery', 'description': 'recovery', 'enable': 1,
             'created': _NOW - 40 * _DAY}]},
        {'userid': 'root@pam', 'entries': [
            {'id': 'c9d0e1f2', 'type': 'yubico', 'description': 'YubiKey 5C', 'enable': 1,
             'created': _NOW - 200 * _DAY}]},
        {'userid': 'bob@pve', 'entries': [
            {'id': '0a1b2c3d', 'type': 'webauthn', 'description': '노트북 보안 키', 'enable': 0,
             'created': _NOW - 3 * _DAY}]},
    ]


@route('GET', r'/access/users/([^/]+)/token')
def tokens(m, query, body):
    return [
        {'tokenid': 'ci', 'privsep': 1, 'expire': 0, 'comment': 'CI 배포'},
        {'tokenid': 'monitor', 'privsep': 0, 'expire': _NOW + 365 * _DAY, 'comment': '모니터링(권한 공유)'},
    ]


@route('GET', r'/pools')
def pools(m, query, body):
    return [
        {'poolid': 'prod', 'comment': '운영 게스트'},
        {'poolid': 'dev', 'comment': '개발 샌드박스'},
        {'poolid': 'backup', 'comment': ''},
    ]


@route('GET', r'/pools/([^/]+)')
def pool(m, query, body):
    members = {
        'prod': [
            {'id': 'qemu/100', 'type': 'qemu', 'vmid': 100, 'node': 'pve1', 'name': 'web-01'},
            {'id': 'lxc/200', 'type': 'lxc', 'vmid': 200, 'node': 'pve2', 'name': 'dns'},
        ],
        'dev': [{'id': 'qemu/102', 'type': 'qemu', 'vmid': 102, 'node': 'pve2', 'name': 'win11-test'}],
    }.get(m.group(1), [])
    members.append({'id': 'storage/pve2/nas', 'type': 'storage', 'storage': 'nas', 'node': 'pve2', 'name': 'nas'})
    return {'members': members}

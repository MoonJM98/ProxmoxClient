# 로그인 직후 공통 요청 — 권한 요약(관리자처럼 모든 권한)
@route('GET', r'/access/permissions')
def permissions(m, query, body):
    privs = ['Sys.Audit', 'Sys.Modify', 'Sys.Console', 'Sys.PowerMgmt', 'VM.PowerMgmt', 'VM.Audit', 'VM.Allocate',
             'VM.Migrate', 'VM.Snapshot', 'VM.Backup', 'VM.Console', 'VM.Config.Disk', 'VM.Config.Options',
             'Datastore.Allocate', 'Datastore.Audit', 'Datastore.AllocateSpace', 'User.Modify', 'Group.Allocate',
             'Pool.Allocate', 'Permissions.Modify', 'Realm.Allocate', 'SDN.Allocate', 'SDN.Audit',
             'Mapping.Modify', 'Mapping.Audit']
    return {'/': {p: 1 for p in privs}}

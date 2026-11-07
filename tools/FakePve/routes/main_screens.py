# 메인 화면(클러스터·작업 로그·연결) 검사용 — 실행 중에 /__fake/set?키=값 으로 서버 동작을 바꾼다.
#   tasklog_delay_ms  작업 로그 응답 지연(느린 서버에서 새로 고침이 겹치는지)
#   tasklog_lines     작업 로그 줄 수(기본 3 — 예전과 같은 세 줄)
#   tasks_forbidden   1 이면 /cluster/tasks 를 403 으로 거부(권한 없음)
#   ticket_delay_ms   로그인(/access/ticket) 지연 — 연결 취소 검사(fake_pve.py 가 읽는다)
#   extra_guests      게스트 N 개를 더한다(긴 목록 가상화 검사)
import time

FLAGS = {}


@route('GET', r'/__fake/set')
def fake_set(m, query, body):
    for key, value in query.items():
        if key == 'extra_guests':
            base = 5000
            for i in range(int(value)):
                vmid = base + i
                STATE.GUESTS.append(dict(id=f'qemu/{vmid}', type='qemu', vmid=vmid, name=f'bulk-{vmid}', node='pve1',
                                         status='stopped', cpu=0, maxcpu=1, mem=0, maxmem=1_073_741_824, disk=0,
                                         maxdisk=10_737_418_240, netin=0, netout=0, uptime=0, template=0))
        else:
            FLAGS[key] = value
            STATE.STATE_FLAGS[key] = value
    return True


@route('GET', r'/cluster/tasks')
def cluster_tasks(m, query, body):
    if FLAGS.get('tasks_forbidden') == '1':
        return STATE.Status(403, None)
    return STATE.TASKS


_FIXED = ['starting task', 'doing things...', 'TASK OK']


@route('GET', r'/nodes/(\w+)/tasks/([^/]+)/log')
def task_log(m, query, body):
    delay = int(FLAGS.get('tasklog_delay_ms', '0'))
    if delay:
        time.sleep(delay / 1000)
    count = int(FLAGS.get('tasklog_lines', '3'))
    lines = _FIXED if count == 3 else [f'line {n:05d}' for n in range(1, count + 1)]
    start = int(query.get('start', '0'))
    limit = int(query.get('limit', '50'))
    return [dict(n=start + i + 1, t=t) for i, t in enumerate(lines[start:start + limit])]

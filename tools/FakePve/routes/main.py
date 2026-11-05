# 데스크톱 메인 화면(Views/Main)이 부르는 API 의 예시 응답 — 그래프(rrddata)에 선이 그려지게 한다.
import math, time

def _series(points, base, amp, phase):
    now = int(time.time())
    out = []
    for i in range(60):
        t = now - (59 - i) * 60
        v = base + amp * math.sin(i / 6.0 + phase)
        out.append({'time': t, 'cpu': max(0.0, min(1.0, v)), 'mem': 0.5 + 0.1 * math.cos(i / 9.0),
                    'maxmem': 1.0, 'netin': 120000 + 9000 * i, 'netout': 40000 + 3000 * i,
                    'diskread': 30000 + 500 * i, 'diskwrite': 12000 + 200 * i})
    return out

@route('GET', r'/nodes/(\w+)/rrddata')
def node_rrd(m, query, body):
    return _series(60, 0.18, 0.08, 0.0)

@route('GET', r'/nodes/(\w+)/(qemu|lxc)/(\d+)/rrddata')
def guest_rrd(m, query, body):
    return _series(60, 0.3, 0.2, 1.0)

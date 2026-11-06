# 관리 화면의 digest 검사 — 게스트 방화벽 규칙 지우기·옮기기·고치기는 목록의 digest 를 함께 보내야 한다.
# 보낸 digest 를 로그('MANAGE fw ...')로 남기고, 지금 digest 와 다르면 실서버처럼 거절한다.
import sys

_MISMATCH = 'detected modified configuration - file changed by other user? Try again.\n'


def _check(method, m, digest):
    current = getattr(STATE, 'FW_GUEST_DIGEST', 'fwdigest1')
    sys.stderr.write("MANAGE fw %s pos=%s digest=%s\n" % (method, m.group(4), digest or '(none)'))
    if digest and digest != current:
        raise STATE.PveError(500, _MISMATCH)


@route('DELETE', r'/nodes/([^/]+)/(qemu|lxc)/(\d+)/firewall/rules/(\d+)')
def fw_guest_rule_delete(m, query, body):
    _check('DELETE', m, query.get('digest'))
    return None


@route('PUT', r'/nodes/([^/]+)/(qemu|lxc)/(\d+)/firewall/rules/(\d+)')
def fw_guest_rule_update(m, query, body):
    _check('PUT', m, body.get('digest'))
    return None


# 다른 사용자가 규칙 파일을 바꾼 것처럼 digest 를 정한다(검사용 — ?value=…, 없으면 처음 값)
@route('GET', r'/__fake/fw-digest')
def fw_digest(m, query, body):
    STATE.FW_GUEST_DIGEST = query.get('value', 'fwdigest1')
    return STATE.FW_GUEST_DIGEST

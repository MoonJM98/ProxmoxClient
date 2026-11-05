# 가짜 PVE 경로 추가

기능을 옮길 때 그 기능이 부르는 API 의 예시 응답을 `routes/<기능>.py` 로 추가한다(다른 파일은 고치지 않는다).

```python
# routes/datacenter_users.py — route, STATE 는 fake_pve.py 가 넣어 준다
@route('GET', r'/access/users')
def users(m, query, body):
    return [{'userid': 'root@pam', 'enable': 1, 'comment': 'Built-in'}]

@route('POST', r'/access/users')
def add_user(m, query, body):
    return None
```

- 경로는 `/api2/json` 뒤의 부분(쿼리 제외), 정규식 전체 일치.
- 반환값이 응답의 `data` 가 된다.
- 등록하지 않은 GET 은 빈 목록(`[]`)을 돌려주고 로그에 `UNHANDLED GET ...` 으로 남는다.

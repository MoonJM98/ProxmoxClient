# 모바일 원격 콘솔 — 조작 방식과 개발 방향

Proxmox VM 화면(VNC)을 휴대폰·태블릿에서 다루는 방법. TeamViewer Remote Control 등 널리 쓰이는 모바일 원격 앱의
조작 관례를 따르고, 태블릿에 연결한 마우스·키보드는 데스크톱과 똑같이 매핑한다.

## 1. 참고한 앱의 조작 관례

| 항목 | TeamViewer Remote Control | RealVNC Viewer | 이 앱 |
|---|---|---|---|
| 기본 모드 | 마우스 모드(손가락 = 터치패드) | 마우스 모드 | 마우스 모드 |
| 모드 정책 | 휴대폰→PC 는 마우스 모드만, 6.5″ 이상 태블릿은 마우스/터치 전환 | 전환 가능 | 둘 다 제공, 하단 막대에서 전환 |
| 탭 | 클릭 | 커서 위치 왼쪽 클릭 | 같음(터치 모드는 누른 곳) |
| 빠른 두 번 탭 | 더블 클릭 | 더블 클릭 | 같음 |
| 두 번 탭 후 끌기 | 끌어다 놓기 | — | 같음 |
| 한 손가락 끌기 | 커서 이동 | 커서 이동 | 같음(터치 모드는 왼쪽 끌기) |
| 길게 누르기 | 문서마다 다름(오른쪽/더블 클릭) — 사용자 불만: 떼기 전에 실행됨 | 누르고 끌기 = 끌기 | **떼면 오른쪽 클릭, 누른 채 끌면 왼쪽 끌기** (떼기 전에는 실행하지 않음) |
| 두 손가락 탭 | — | 오른쪽 클릭 | 오른쪽 클릭 |
| 세 손가락 탭 | — | 가운데 클릭 | 가운데 클릭 |
| 두 손가락 끌기 | 스크롤 | 스크롤(가로 포함) | 스크롤(가로 포함) |
| 벌리기·오므리기 | 확대·축소 | 확대·축소 | 확대·축소(+ 이동), 마우스 모드는 커서를 따라 화면 이동 |
| 키보드 | 하단 막대 키보드 버튼 | 툴바 + 특수키 줄 | 하단 막대: ⌨ · Esc · Tab · Ctrl/Alt/Win 고정(1회) · 방향키 · ⋯(F1~F12, Home/End, PgUp/PgDn, Ins/Del, Ctrl+Alt+Del) |

출처: TeamViewer 지식 베이스 *Touch Interactions vs Mouse Interactions on Android*, *Interaction Methods on iOS*,
RealVNC 도움말 *Using RealVNC Viewer for Mobile*, Support.com TeamViewer 사용 안내.

## 2. 구조

| 부분 | 파일 | 역할 |
|---|---|---|
| 제스처 해석 | `ProxmoxClient.UI/Views/Console/GestureInterpreter.cs` | 터치 → RFB 마우스 이벤트(화면과 분리된 순수 로직이라 그대로 시험) |
| 화면 배치 | `Viewport.cs` | 화면 맞춤·확대(최대 5배)·이동, 커서 따라가기 |
| 화면 | `VncSurface.cs` | 프레임버퍼 → GPU 비트맵(바뀐 영역만, 한 프레임에 한 번), 입력 분기(터치 / 마우스 / 키보드) |
| 물리 키 | `PhysicalKeys.cs` | 키 위치 → XT 스캔코드(WPF 콘솔과 같은 방식, QEMU 확장 키) + keysym |
| 화면 페이지 | `ConsolePage.cs` | 하단 특수키 막대, 소프트 키보드, 모드 전환 |
| 통신 | Core `ProxmoxVncSession` | WPF 와 같은 vncproxy + 웹소켓 + RFB(Tight JPEG 는 Skia 로 디코딩) |

## 3. 입력 장치별 규칙

- **터치**: 위 표의 제스처. 손가락 포인터만 해석기로 보낸다.
- **마우스(데스크톱 · 태블릿 USB/블루투스 마우스)**: 해석하지 않고 그대로 — 왼/가운데/오른쪽 버튼, 휠(세로·가로),
  끄는 동안 포인터 잡기. Android 가 마우스 오른쪽 버튼을 '뒤로 가기'로 바꾸는 것은 `MainActivity` 가 막는다.
- **물리 키보드(데스크톱 · 태블릿)**: 키 위치(스캔코드) + keysym 을 함께 보낸다 → 게스트의 키보드 배열로 해석되어
  단축키·한/영·한자 키가 그대로 동작. 눌린 키를 기억해 포커스를 잃거나 화면을 떠나거나 앱이 배경으로 가면 모두 뗀다.
  같은 키의 글자 입력(TextInput)은 다시 보내지 않는다(중복 입력 방지).
- **소프트 키보드(IME)**: 물리 키 정보가 없으므로 완성된 글자를 keysym(유니코드)으로 보낸다. 고정해 둔 Ctrl·Alt·Win 은 다음 한 키에만 적용.
- **서버 상태를 바꾸는 동작**(Ctrl+Alt+Del)은 확인 후 보낸다.

## 4. 지연 최소화

- Android 렌더링: Vulkan → OpenGL ES → 소프트웨어 순으로 시도.
- 바뀐 영역만 모아 화면 갱신 주기마다 한 번 복사, 연속 포인터 이동은 Core 가 마지막 좌표로 병합.
- 전체 AOT(실행 중 JIT 없음), .NET HTTP 처리기.

## 5. 조작성 검사(자동)

`tools/UiSnapshot` 의 `--target console` 이 가짜 PVE(`tools/FakePve`, RFB 서버 포함)에 연결해 제스처·마우스·키보드를
흉내 내고, **서버가 실제로 받은 이벤트**(로그의 `RFB pointer/key/qkey`)를 기대값과 비교한다.

```bash
python3 -I tools/FakePve/fake_pve.py 8106 > /tmp/fp.log 2>&1 &
dotnet run --project tools/UiSnapshot -- --port 8106 --out /tmp/shots --mode mobile --target console --serverlog /tmp/fp.log
```

현재 26개 항목(터치 13 · TeamViewer 끌어다 놓기 · 물리 마우스 4 · 특수키/소프트 키보드 3 · 물리 키보드 5 · 포커스 해제) 모두 통과.

## 6. 남은 일

- 실제 기기(휴대폰·태블릿 + 블루투스 키보드/마우스)에서 체감 확인 — 제스처 임계값(탭 12px, 길게 누르기 0.45초, 스크롤 28px) 조정.
- 터치 조작 도움말 화면(TeamViewer 처럼 첫 연결 때 제스처 안내).
- CT·노드 셸 터미널(라이선스가 확인된 터미널 엔진 필요).
- 게스트 클립보드 동기화 버튼, 화면 해상도 맞춤(RequestDesktopSize).

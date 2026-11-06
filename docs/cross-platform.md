# 크로스플랫폼 앱 (Avalonia) — 구조와 이관 계획

기존 WPF 앱(`ProxmoxClient.App`, Windows 전용)을 Avalonia 12 로 옮기는 중이다.
**한 벌의 화면·로직 코드**를 Windows · Linux · macOS · Android 가 함께 쓰고, 운영체제마다 다른 부분만 분기한다.
WPF 앱은 기능이 모두 옮겨질 때까지 그대로 유지한다.

## 프로젝트

| 프로젝트 | 대상 | 역할 |
|---|---|---|
| `ProxmoxClient.Core` | 공용 | 서버 API·로그인·인증서 고정·콘솔 프로토콜 (WPF 와 공유, 수정 없음) |
| `ProxmoxClient.UI` | 공용 | Avalonia 화면·뷰모델·서비스. OS 분기는 `Platform/` 에만 둔다 |
| `ProxmoxClient.Desktop` | Windows · Linux · macOS | 데스크톱 시작 코드(실행 파일 `ProxmoxClient`) |
| `ProxmoxClient.Android` | Android | 액티비티·키스토어 비밀값 저장소·리소스 |
| `ProxmoxClient.App` | Windows | 기존 WPF 앱 (이관 완료 후 제거) |

솔루션: 데스크톱 WPF 는 `ProxmoxClient.slnx`, Avalonia 는 `ProxmoxClient.CrossPlatform.slnx`
(Windows WPF 빌드에 Android 워크로드가 필요하지 않게 나눴다).

## 운영체제별 분기

| 항목 | Windows | macOS | Linux | Android |
|---|---|---|---|---|
| 비밀값 저장 | 자격 증명 관리자 | 로그인 키체인 | Secret Service (`secret-tool`) | 키스토어 AES-256-GCM |
| 화면 배치 | 메뉴 · 목록 · 상세 분할 | 같음 | 같음 | 탭 + 페이지 스택 |
| 프로필 폴더 | `%APPDATA%\ProxmoxClient\profiles` (WPF 와 같은 곳 — 기존 프로필을 그대로 읽는다) | `~/.config/ProxmoxClient/profiles` | 같음 | 앱 전용 폴더 |

- 비밀값 저장소 선택: `ProxmoxClient.UI/Platform/SecretStores.cs` 한 곳. 저장소를 쓸 수 없으면(예: Linux 에 `secret-tool` 없음)
  메모리에만 두고 "저장" 선택지를 숨긴다.
- 화면 배치: 같은 뷰모델을 `PageFactory` 가 형태(`PlatformInfo.FormFactor`)에 따라 다른 페이지로 보여 준다.
  상세 화면(게스트·노드·작업 로그)은 UserControl 하나를 모바일 페이지와 데스크톱 상세 창이 함께 쓴다.
- 새 OS 전용 기능은 `Platform/` 에 인터페이스 + OS 별 구현으로 넣고, 화면 코드에서 `OperatingSystem.IsXxx()` 를 직접 쓰지 않는다.

## 지연 최소화

| 항목 | Windows | macOS | Linux | Android |
|---|---|---|---|---|
| 화면 렌더링(우선순위) | Direct3D(ANGLE) + 저지연 DXGI 스왑 체인 → Vulkan → 소프트웨어 | Metal → OpenGL → 소프트웨어 | Vulkan → EGL → GLX → 소프트웨어 | **Vulkan** → OpenGL ES(EGL) → 소프트웨어 |
| 코드 실행 | JIT(.NET) | 같음 | 같음 | 전체 AOT(실행 중 JIT 없음) |
| HTTP | .NET SocketsHttpHandler | 같음 | 같음 | 같음(Android 기본 Java 처리기 대신 — 연결 재사용·인증서 고정·SOCKS5 가 데스크톱과 동일) |

- 렌더링 설정: 데스크톱 `ProxmoxClient.Desktop/Program.cs`, Android `ProxmoxClient.Android/MainApplication.cs`
  (플랫폼 옵션 형식이 헤드 프로젝트에만 있어 각 헤드에 둔다). 앞의 방식을 못 쓰는 기기는 자동으로 다음 방식으로 넘어간다.
- 네트워크: 로그인 직후 첫 화면 데이터를 버전 확인과 동시에 미리 요청하고, 상세 화면의 사용량·실시간 상태도 동시에 요청한다.
  작업 완료 확인은 0.5초 간격.

### 고속 경로(hot path)

| 경로 | 하는 일 | 위치 |
|---|---|---|
| 원격 화면(VNC/RDP) 수신 | 수신 스레드에서 디코딩(Tight JPEG 은 Skia) → 프레임버퍼에 바로 쓴다. 화면 갱신은 바뀐 사각형만 표시 | `Core/Vnc`, `Views/Console/SkiaImageDecoder.cs` |
| 원격 화면 업로드 | 256×256 타일 비트맵 — 바뀐 타일만 GPU 로 올린다. 큰 갱신은 타일 복사를 병렬로. 타일 경계는 픽셀에 맞춰 확대해도 이음매가 없다 | `Views/Console/TiledFramebuffer.cs` |
| 원격 화면 그리기 | 1.5배 이상 확대는 최근접(글자 선명), 그 아래는 중간 품질 보간. 그리기는 위 표의 GPU 백엔드가 맡는다 | `Views/Console/VncSurface.cs` |
| 입력 | 물리 키는 XT 스캔코드(QEMU 확장 키)로, 터치 제스처는 해석기가 포인터 이벤트로. 포커스를 잃거나 앱이 배경으로 가면 눌린 키를 모두 뗀다 | `Views/Console/GestureInterpreter.cs`, `PhysicalKeys.cs` |
| 터미널 출력 | 수신 스레드에서 VT 해석(화면 스레드 막지 않음) → 다시 그리기는 한 프레임에 한 번으로 모은다 | `Views/Terminal/TerminalView.cs` `Write` |
| 터미널 그리기 | 같은 모양이 이어지는 글자를 GlyphRun 한 번으로, 바탕은 같은 색 구간을 사각형 하나로. 대체 글꼴(한글) 글리프는 캐시. 선·블록 문자는 도형 | `Views/Terminal/TerminalRenderer.cs`, `BoxDrawing.cs` |
| 터미널 크기 | 창 크기를 바꾸는 동안 바뀐 크기를 모아 한 번만 PTY 에 보낸다(같은 크기는 세션이 거른다) | `Views/Terminal/TerminalPage.cs` |
| 터미널 대체 글꼴 | JetBrains Mono → Pretendard(한글) → 이모지는 기기 컬러 이모지 먼저 → DejaVu Sans Mono·Noto Sans Math·Symbols 1/2·Symbols Nerd Font Mono → Noto Sans Mono CJK KR(한자·가나·중국어, 16MB — 그런 글자가 처음 나올 때 읽음) → 기기 글꼴 → Noto Emoji(흑백) → 네모 | `Views/Terminal/TerminalFonts.cs`, `Assets/TerminalFonts`, `Assets/CjkFonts` |
| 터미널 다시 감기 | 폭이 바뀌면 이어진 줄(자동 줄바꿈)을 다시 감는다 — 좁혔다 넓히면 원래대로. 커서·저장 커서·보던 위치 유지. PTY 크기는 120ms 모아 보냄 | `Views/Terminal/TerminalBuffer.Reflow.cs`, `TerminalPage.cs` |
| 터미널 글자 | 유니코드 16 폭 표(생성: `tools/TerminalTests/gen_width.py`), 결합 문자·NFD 한글·ZWJ 이모지·국기 묶음, 기울임·밑줄 모양(4:n)·밑줄 색(58)·윗줄, 색 OSC 4/10/11/12/104/110~112, 화면 반전 ?5, 깜박임 | `TerminalCell.cs`, `TerminalRenderer.cs`, `BoxDrawing.cs` |
| 터미널 입력 | 키보드 앱 붙여넣기도 괄호 붙여넣기, 고정 Shift(⇧Tab), AltGr, 휠 한 칸=보고 한 번, 마우스 수식키·?1003 움직임·좌표 95 제한, 포커스 보고 ?1004, ?1007, 숫자패드 응용 모드, 4KB 씩 나눠 보내기 | `TerminalKeys.cs`, `TerminalView.cs`, `ProxmoxTerminalSession.cs` |
| 터미널 질의 응답 | 창 크기(CSI 18t)·배경색(OSC 10/11)·터미널 이름(XTGETTCAP)·모드(DECRQM)·커서 위치에 바로 답한다 — systemd 는 콘솔을 열 때 0.33초씩만 기다려, 답이 없거나 늦으면 로그인 칸에 `^[[24;80R` 이 찍힌다 | `Views/Terminal/TerminalBuffer.cs` |
| 터미널 동기화 출력 | DEC 2026(Claude Code·neovim 등이 다시 그릴 때 켜고 끈다) 동안 그리지 않고, 끄는 신호가 0.2초 안에 안 오면 그냥 그린다. 모드 질의(DECRQM)에 답한다 | `Views/Terminal/TerminalView.cs` `Redraw`, `TerminalBuffer.cs` `ReportMode` |
| 터미널 손가락 선택 | 길게 눌러 단어 선택 → 양 끝 손잡이(맨 아랫줄이면 위로)를 끌어 넓히기·줄이기, 끄는 동안 손가락 위 확대경. 프로그램이 마우스를 받으면(Claude Code 전체 화면·htop) 손가락 끌기는 휠로 보낸다 | `Views/Terminal/TerminalView.cs` |
| 목록 새로 고침 | 로그인 직후 첫 화면 데이터 미리 요청, gzip 응답, 같은 요청은 동시에 | `ViewModels/ProfilesViewModel.cs`, `Core/Api` |

렌더링 방식은 첫 화면(로그인·서버 목록) 아래에 작게 보인다(예: `Vulkan · Android · Mobile · 1.0.3`). 데스크톱은
`PROXMOXCLIENT_LOG=1` 로 실행하면 각 GPU 경로를 시도한 결과가 표준 오류에 남는다.
GPU 경로가 초기화는 되는데 창이 검게 나오면 `PROXMOXCLIENT_RENDER=software` 로 CPU 렌더링을 강제한다(모든 데스크톱 OS).

### 실제 연결 오류 처리

| 상황 | 처리 |
|---|---|
| 티켓 만료 | 만료 전(60분) 자동 갱신, 401 이면 한 번 갱신 후 재시도. 갱신까지 거부되면 한 번 알리고 로그인 화면으로 |
| 처음 보는·바뀐 인증서 | 지문 확인(TOFU) — 콘솔 웹소켓도 같은 검증기 |
| 콘솔·터미널 끊김 | 상태를 보이고 [다시 연결](새 티켓·새 세션) |
| IPv6 주소 | `[fe80::1]`·영역 구분자(`%eth0` → `%25`) 처리, IPv6 이면 인증 쿠키를 머리글로 |
| API 토큰 로그인 | 콘솔 티켓이 없어 콘솔·터미널 단추를 끈다 |

## 안전 규칙

서버 상태를 바꾸는 작업(전원, 스냅숏 만들기·되돌리기·삭제 등)은 **모두 확인을 받는다.**
뷰모델의 `ConfirmAndRunAsync(Confirmation, …)` 가 유일한 실행 경로이고, 확인 문구가 필수 인자라 확인 없이 실행되는 경로가 없다.
새 작업을 추가할 때도 이 경로를 쓴다.

## 이관 현황

| 단계 | 내용 | 상태 |
|---|---|---|
| 1 | 공용 UI·데스크톱/Android 헤드·OS 분기 · 프로필/로그인/인증서 · 게스트·노드·스토리지·작업 목록 · 게스트 전원·스냅숏·IP·설정 보기 · 노드 상태 · 작업 로그 | 완료 |
| 2 | 게스트 창: 탭·하드웨어·옵션·백업·방화벽·클라우드 이니트·에이전트 도구, 복제·마이그레이션 | 완료 |
| 3 | 콘솔: VNC(타일 업로드), 터미널(자체 VT 에뮬레이터 — 노드 셸·CT 콘솔·VM 직렬), RDP, SPICE 실행(OS별) | 완료 |
| 4 | 노드: 셸·시스템 로그·디스크·네트워크·서비스·업데이트(업그레이드 셸)·인증서·Ceph(설치 셸) | 완료 |
| 5 | 데이터센터: 권한·사용자·영역·2단계 인증·HA·SDN·알림·백업 작업·옵션·풀 | 완료 |
| 6 | 스토리지 탐색·업로드·가져오기, VM/CT 생성 마법사 | 완료 |
| 7 | 게스트 파일(에이전트·SFTP·SMB·FTP), SSH 키, 앱·콘솔 설정, OpenID 로그인, VPN(OpenVPN, 데스크톱) | 완료 |
| 8 | 실기기 확인(Android 기기·Windows·macOS), 설치 관리자 교체, WPF 앱 제거 | 예정 |

## 검증 현황

| 대상 | 방법 | 결과 |
|---|---|---|
| 화면 전체(데스크톱·태블릿·모바일 배치) | UiSnapshot + 가짜 PVE(`tools/FakePve`) | 모든 대상 통과 |
| VNC 콘솔 조작(제스처·물리 키·화면 키보드 Shift/두벌식·클립보드) | `--target console` | 모바일·데스크톱 통과 |
| 해석기·순수 논리 | `dotnet run --project tools/TerminalTests` (터미널 제어열·질의·유니코드·다시 감기·입력 부호, RDP 키, 2단계 인증·보안 키(CTAP2·PIN·U2F, 소프트웨어 키), 관리 입력 검사, 터미널 다시 열기 등) | 687/687 |
| Core 단위 검사 | `dotnet test tests/ProxmoxClient.Core.Tests` (RFB 디코딩·VNC 인증·PVE 응답·저장소·UPID) | 60/60 |
| RDP 실제 서버 | xrdp·FreeRDP(NLA) + 가짜 PVE rdpproxy(RDCleanPath) + `--target rdp` (IronRDP 2026.10.29), Windows Server 2025(러너 자신, NLA 필수) + `tools/WinRdpE2E` | 연결·CredSSP/NTLM(HYBRID·HYBRID_EX)·로그인 실패 안내·화면·입력·한글 유니코드 키·해상도 맞춤·다시 연결·Windows 클립보드 양방향 통과 |
| 보안 키(USB, Linux) | GitHub 러너의 `/dev/uhid` 가상 FIDO 키(`tools/VirtualFido`) + 실제 `LinuxHidWebAuthnProvider` | CTAP2·PIN v1/v2·U2F·취소·뽑기·로그인 12개 통과. 실제 키·NFC·Windows/macOS 경로는 실물 확인 필요 |
| 로그인 | `--target tfa`·`--target webauthn`(가짜 키)·`--target restore` | 통과 — 실제 보안 키는 미확인 |
| CI | `.github/workflows/ci.yml` (Linux 빌드·검사·화면·RDP, Windows WPF, Android APK, macOS Desktop·iOS) | 모두 통과 |
| 터미널 해석·입력 | `--target terminal` (가짜 셸: + 손잡이 선택·동기화 출력·마우스 보고 중 스크롤) | 모바일 20/20 |
| 터미널 + 실제 프로그램 | `FAKEPVE_REAL_SHELL=1` + `--target realshell` (진짜 bash PTY: stty·vim·top·Ctrl+C·Tab 완성·Tab 후보 목록·최근 명령 기록·5만 줄 출력·크기·키보드로 줄 때 대체 화면 크기·종료) | 18/18 |
| CT 파일(노드 셸 스크립트) | 진짜 bash + pct/lxc-info 심, `preview:files` | 목록·링크 정상 |
| 세션 만료 | `/__fake/expire` + `--target expiry` | 알림 → 로그인 화면 |
| VPN | 실제 OpenVPN 2.6 서버(자체 CA·tun) + 터널 주소에만 묶은 가짜 PVE, `--vpn` | 터널 → 로그인 → 종료 시 정리, 강제 종료 뒤 남은 openvpn 정리 |
| Linux 실제 데스크톱 | 릴리스 빌드를 Xvfb(X11) 에서 실행, xdotool 로 조작 | 인증서 확인·로그인·메인·CT 터미널(vim·top)·VNC 콘솔(물리 키 → 스캔코드) 정상 |
| Linux 렌더링 경로 | 같은 실행에서 `PROXMOXCLIENT_LOG=1` 로 초기화 기록 확인 | GPU 없음: Vulkan(드라이버 없음) → EGL(libEGL 없음) → GLX(llvmpipe 는 Avalonia 가 거부) → 소프트웨어로 정상 전환. Mesa lavapipe 를 깔면 **Vulkan** 으로 그리며 로그인·메인·CT 터미널·VNC(타일 업로드·확대)·휠 입력 정상 |
| Windows 빌드(Wine 9, Xvfb) | win-x64 자체 포함 빌드를 Wine 으로 실행 | Win32 플랫폼으로 시작. ANGLE(D3D11) 은 Wine 의 D3D 변환에서 창이 검게 나옴(Wine 한계로 보임 — 실제 Windows 미확인) → `PROXMOXCLIENT_RENDER=software` 로 로그인 화면 정상(라벨 'Software · Windows · Desktop'). 가짜 서버에 Windows 빌드로 접속: 자체 서명 인증서 확인 창 → 신뢰 → 영역 목록 받기 → 틀린 비밀번호는 '사용자 이름·비밀번호·영역이 맞지 않습니다' → 맞는 비밀번호로 메인(게스트 5·노드 2, 자동 새로고침, 상세 그래프) → 더블클릭으로 VNC 콘솔(웹소켓·화면·커서 모양, 클릭 mask=1, 키 a/b → QEMU 스캔코드 0x1e/0x30) |
| Android 실제 동작 | GitHub Actions 에뮬레이터(API 35, x86_64) + 디버그 전용 자동화 고리 + `tools/AndroidE2E/e2e.sh` (`.github/workflows/android-e2e.yml`) | 35개 통과 — 안전 영역(제스처·3버튼), 액티비티 재생성 뒤 같은 셸, 연결 유지 서비스·알림·[모두 끊기]·연결 직후 끊김 10회, 마우스 우클릭(uinput)·뒤로 가기, 화면 보호. 알려진 것: 보호한 채 최근 앱에서 돌아온 뒤 끄면 앱을 나갈 때까지 캡처가 검다(시스템 최근 앱 상태 — 앱 밖) |
| Windows·macOS | 빌드(WPF·데스크톱 헤드) | 실기기 확인 필요(Direct3D/ANGLE·Metal 경로, Credential Manager·Keychain, remote-viewer·openvpn 실행) |
| 실제 Proxmox 서버 | — | 실서버 확인 필요(가짜 서버는 PVE 9.0 API 응답 형식을 따른다) |

## 배포 파일

| OS | 만드는 법 | 비고 |
|---|---|---|
| Android | `dotnet publish ProxmoxClient.Android -c Release` | arm64 + x64 한 APK, 전체 AOT |
| macOS | `tools/packaging/make-macos-app.sh arm64` (또는 `x64`) — Linux·Windows 에서도 됨 | `Proxmox Client.app` zip. 실행 파일은 임시(ad-hoc) 서명, 개발자 ID 서명·공증은 macOS 에서. 서명 없이 받은 앱은 처음 한 번 `xattr -cr "/Applications/Proxmox Client.app"` |
| Windows · Linux | `dotnet publish ProxmoxClient.Desktop -c Release -r win-x64` / `linux-x64` | |
| iOS · iPadOS | `ProxmoxClient.iOS` (Mac 에서 `ProxmoxClient.Apple.slnx`) — 아래 "iOS 빌드" | iPhone = 휴대폰 배치, iPad = PC 배치, Metal 렌더링, 키체인. 서명·배포 설정은 아직 없음 |

### iOS 빌드(Mac 에서)

1. Xcode 설치 후 `dotnet workload install ios`
2. 시뮬레이터: `dotnet build ProxmoxClient.iOS -t:Run -p:RuntimeIdentifier=iossimulator-arm64`
3. 기기(iPhone·iPad): Xcode 에 Apple 계정을 넣고 `dotnet build ProxmoxClient.iOS -t:Run -p:RuntimeIdentifier=ios-arm64 -p:CodesignKey="Apple Development: ..."`
   — 무료 계정은 7일마다 다시 설치, 개발자 계정이면 TestFlight·App Store 배포 가능
4. iOS 전용 코드(시작·키체인)는 Linux 에서도 iOS 참조 어셈블리로 형식 검사를 통과했다(실제 연결·서명은 Mac 에서).

## 빌드

필요: .NET 10 SDK. Android 는 추가로 `android` 워크로드, Android SDK(플랫폼 36), JDK 17+.

```bash
# 데스크톱 (현재 OS 용)
dotnet run --project ProxmoxClient.Desktop

# Android
dotnet workload install android
dotnet build ProxmoxClient.Android -c Debug
dotnet publish ProxmoxClient.Android -c Release -f net10.0-android   # 설치용 APK
```

Android SDK 위치를 자동으로 찾지 못하면 `-p:AndroidSdkDirectory=<경로>` 를 붙인다.

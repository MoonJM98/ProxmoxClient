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

## 안전 규칙

서버 상태를 바꾸는 작업(전원, 스냅숏 만들기·되돌리기·삭제 등)은 **모두 확인을 받는다.**
뷰모델의 `ConfirmAndRunAsync(Confirmation, …)` 가 유일한 실행 경로이고, 확인 문구가 필수 인자라 확인 없이 실행되는 경로가 없다.
새 작업을 추가할 때도 이 경로를 쓴다.

## 이관 현황

| 단계 | 내용 | 상태 |
|---|---|---|
| 1 | 공용 UI·데스크톱/Android 헤드·OS 분기 · 프로필/로그인/인증서 · 게스트·노드·스토리지·작업 목록 · 게스트 전원·스냅숏·IP·설정 보기 · 노드 상태 · 작업 로그 | 완료 |
| 2 | 게스트 탭: 요약 그래프(RRD)·하드웨어·옵션 편집·백업·방화벽·작업, 복제·마이그레이션 | 예정 |
| 3 | 콘솔: VNC(공용 렌더러 + Windows Direct2D 고속 경로), 터미널(xterm), RDP(IronRDP), SPICE 실행 | 예정 |
| 4 | 노드: 셸·시스템 로그·디스크·네트워크·서비스·업데이트·인증서·Ceph, 노드 전원 | 예정 |
| 5 | 데이터센터: 권한·사용자·영역·2단계 인증·HA·SDN·알림·백업 작업·옵션·풀 | 예정 |
| 6 | 스토리지 탐색·업로드·가져오기, VM/CT 생성 마법사 | 예정 |
| 7 | 게스트 파일(에이전트·SFTP·SMB·FTP), SSH 키, VPN, OpenID 로그인, 앱 설정 | 예정 |
| 8 | 설치 관리자 교체, WPF 앱 제거 | 예정 |

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

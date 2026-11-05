# Proxmox Client — Android

Avalonia 12 로 만든 Android 앱. 서버 통신·모델은 데스크톱(WPF) 앱과 같은 `ProxmoxClient.Core` 를 그대로 쓴다.

| 프로젝트 | 역할 |
|---|---|
| `ProxmoxClient.Mobile` | 공용 UI(Avalonia) — 화면, 뷰모델, 서비스 |
| `ProxmoxClient.Mobile.Android` | Android 헤드 — 액티비티, 키스토어 비밀값 저장소, 리소스 |

## 기능

- 서버 프로필 추가·편집·삭제 (비밀번호 / API 토큰)
- 비밀번호·토큰을 Android 키스토어(AES-256-GCM)로 암호화해 기기에 저장(선택)
- 자체 서명 인증서 지문 확인 후 고정(TOFU) — 데스크톱과 같은 방식
- 게스트(VM·CT) 목록: 검색, 실행 중/중지 필터, CPU·메모리 사용률, 당겨서 새로 고침
- 노드·스토리지·최근 작업 탭, 작업 로그
- 게스트 상세: 시작·종료·재부팅·강제 정지·일시정지·재개·리셋, IP 주소, 스냅숏(만들기·되돌리기·삭제), 설정 보기
- 노드 상세: CPU·부하·메모리·스왑·루트 디스크, 노드의 게스트
- 화면이 보이는 동안 자동 새로 고침(앱이 배경이면 멈춤), 밝게/어둡게 테마, 한국어/영어

## 빌드

필요: .NET 10 SDK, `android` 워크로드, Android SDK(플랫폼 36), JDK 17+.

```bash
dotnet workload install android
dotnet build ProxmoxClient.Mobile.Android -c Debug
# 설치용 APK
dotnet publish ProxmoxClient.Mobile.Android -c Release -f net10.0-android
```

Android SDK 위치를 자동으로 찾지 못하면 `-p:AndroidSdkDirectory=<경로>` 를 붙인다.
데스크톱 솔루션(`ProxmoxClient.slnx`)과 분리해 `ProxmoxClient.Mobile.slnx` 에 묶었다 — Windows 빌드에 Android 워크로드가 필요하지 않게.

# 서드파티 고지

ProxmoxClient 는 MIT 라이선스로 배포되며(루트의 `LICENSE` 참조), 아래 서드파티 구성요소를 포함합니다.
각 구성요소의 저작권과 라이선스 조건은 원 저작권자에게 있습니다.

각 구성요소가 자체적으로 포함하는 서드파티의 전체 고지는 `third-party/` 폴더에 원문 그대로 두었습니다.

| 구성요소 | 라이선스 | 원문 고지 |
| --- | --- | --- |
| CommunityToolkit.Mvvm 8.4.2 | MIT | `third-party/communitytoolkit-mvvm-License.md`<br>`third-party/communitytoolkit-mvvm-ThirdPartyNotices.txt` |
| Windows Terminal (Microsoft.Terminal.Wpf / Control) | MIT | `third-party/windows-terminal-NOTICE.md` |
| Lucide Icons | ISC (일부 Feather MIT) | `third-party/lucide-LICENSE.txt` |

---

## 1. CommunityToolkit.Mvvm 8.4.2

- 저작권: (c) .NET Foundation and Contributors. All rights reserved.
- 라이선스: MIT
- 출처: https://github.com/CommunityToolkit/dotnet
- 포함 형태: NuGet 패키지 참조(`CommunityToolkit.Mvvm.dll` 배포)

이 라이브러리는 DeferredEvents, ComputeSharp 등 7개 프로젝트의 코드를 포함합니다.
전체 목록과 각 라이선스 전문은 `third-party/communitytoolkit-mvvm-ThirdPartyNotices.txt`
(패키지에 동봉된 원문)에 있습니다.

---

## 2. Windows Terminal (Microsoft.Terminal.Wpf / Microsoft.Terminal.Control)

- 저작권: (c) Microsoft Corporation. All rights reserved.
- 라이선스: MIT
- 출처: https://github.com/microsoft/terminal
- 포함 형태: 저장소에 담은 패키지
  `packages/proxmoxclient.vendored.terminal.wpf.1.25.2603.3002.nupkg` 를 통해 참조하며,
  다음 파일이 배포에 포함됩니다.
  - `Microsoft.Terminal.Wpf.dll` (관리 어셈블리)
  - `runtimes/win-x64/native/Microsoft.Terminal.Control.dll`
  - `runtimes/win-x86/native/Microsoft.Terminal.Control.dll`
  - `runtimes/win-arm64/native/Microsoft.Terminal.Control.dll`

Microsoft 는 이 컨트롤을 nuget.org 에 게시하지 않으므로(microsoft/terminal 이슈 #6999 에서
제품화 작업이 진행 중입니다) MIT 바이너리를 저장소에 직접 담아 씁니다
(`packages/` + 루트 `nuget.config` 의 packageSourceMapping).

바이너리는 수정하지 않았으며 Microsoft Authenticode 서명이 그대로 유지됩니다.

```
Company : Microsoft Corporation      Product   : Windows Terminal
Version : 1.25.2603.03002            SigStatus : Valid
Signer  : CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US
```

### 네이티브 코어에 포함된 서드파티

`Microsoft.Terminal.Control.dll` 은 아래 구성요소를 포함합니다. MIT 외에 BSD·Apache 2.0·
퍼블릭 도메인이 섞여 있으므로, 전체 고지 원문을 `third-party/windows-terminal-NOTICE.md`
(배포 바이너리와 같은 커밋 `9ae724aa` 기준)에 그대로 두었습니다.

| 라이선스 | 구성요소 |
| --- | --- |
| MIT | jsoncpp, {fmt}, interval_tree, PCG Random, Oklab, fzf, GSL, Microsoft-UI-XAML, VirtualDesktopUtils, wil |
| BSD 3-Clause | chromium/base/numerics |
| BSD 2-Clause | cmark |
| Apache 2.0 | ColorBrewer |
| Unlicense (퍼블릭 도메인) | wyhash, stb |

---

## 3. Lucide Icons

- 저작권: Copyright (c) Lucide Icons and Contributors
  (Feather 에서 파생된 일부 아이콘은 Copyright (c) 2013-2022 Cole Bemis, MIT)
- 라이선스: ISC
- 출처: https://lucide.dev
- 포함 형태: `ProxmoxClient.App/Themes/Icons.xaml` 의 아이콘 경로 데이터 37개

라이선스 전문과 Feather 파생 아이콘 목록은 `third-party/lucide-LICENSE.txt` 에 있습니다.

---

## 함께 사용하지만 포함하지 않는 소프트웨어

아래 프로그램은 배포물에 **포함되지 않으며**, 사용자의 PC 에 설치된 것을 별도 프로세스로
실행하거나 공식 배포처로 안내만 합니다. 이 소프트웨어들의 라이선스는 ProxmoxClient 에
적용되지 않습니다.

| 프로그램 | 라이선스 | 연동 방식 |
| --- | --- | --- |
| OpenVPN | GPLv2 | 설치된 `openvpn.exe` 실행 및 관리 인터페이스 통신 |
| Virt Viewer (`remote-viewer`) | GPLv2 | 공식 배포처(releases.pagure.org) 안내 후 실행 |
| Proxmox VE | AGPLv3 | HTTPS REST API 호출 |

.NET 및 WPF 런타임은 배포물에 포함되지 않습니다(프레임워크 종속 배포).
터미널 글꼴(Cascadia Mono, Consolas, D2Coding, NanumGothicCoding, Lucida Console,
Courier New)은 이름으로만 참조하며 글꼴 파일을 포함하지 않습니다.

---

## MIT License

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## ISC License

```
Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR
OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.
```

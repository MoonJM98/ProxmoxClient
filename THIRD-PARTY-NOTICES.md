# 서드파티 고지

ProxmoxClient 는 MIT 라이선스로 배포되며(루트의 `LICENSE` 참조), 아래 서드파티 구성요소를 포함합니다.
각 구성요소의 저작권과 라이선스 조건은 원 저작권자에게 있습니다.

각 구성요소가 자체적으로 포함하는 서드파티의 전체 고지는 `third-party/` 폴더에 원문 그대로 두었습니다.

| 구성요소 | 라이선스 | 원문 고지 |
| --- | --- | --- |
| CommunityToolkit.Mvvm 8.4.2 | MIT | `third-party/communitytoolkit-mvvm-License.md`<br>`third-party/communitytoolkit-mvvm-ThirdPartyNotices.txt` |
| Windows Terminal (Microsoft.Terminal.Wpf / Control) | MIT | `third-party/windows-terminal-NOTICE.md` |
| Lucide Icons | ISC (일부 Feather MIT) | `third-party/lucide-LICENSE.txt` |
| Net.Codecrete.QrCodeGenerator 3.2.1 | MIT | `third-party/qrcodegenerator-LICENSE.txt` |
| IronRDP (Devolutions.IronRdp 2025.12.4) | MIT 또는 Apache-2.0 (선택) | `third-party/ironrdp-LICENSE-MIT.txt`<br>`third-party/ironrdp-LICENSE-APACHE.txt` |
| Vortice.Windows 3.8.3 · SharpGen.Runtime 2.4.2-beta | MIT | 아래 MIT 전문 |
| FluentFTP 55.1.0 | MIT | 아래 MIT 전문 |
| Pretendard 1.3.9 (글꼴) | SIL OFL 1.1 | `ProxmoxClient.UI/Assets/Fonts/Pretendard-LICENSE.txt` |
| JetBrains Mono (글꼴) | SIL OFL 1.1 | `ProxmoxClient.UI/Assets/Fonts/JetBrainsMono-LICENSE.txt` |
| SSH.NET 2026.0.0 · BouncyCastle.Cryptography 2.7.0 | MIT | 아래 MIT 전문<br>`third-party/bouncycastle-LICENSE.md` |
| Avalonia 12.1.5 (및 Avalonia.Controls.DataGrid 12.1.2 등) | MIT | 아래 MIT 전문 |
| SkiaSharp 3.119.4 · HarfBuzzSharp 8.3.1.3 (네이티브 Skia·HarfBuzz·FreeType 등 포함) | MIT (+ 포함 구성요소별 라이선스) | `third-party/skiasharp-LICENSE.txt`<br>`third-party/skiasharp-harfbuzzsharp-THIRD-PARTY-NOTICES.txt` |
| ANGLE (Avalonia.Angle.Windows.Natives, Windows 데스크톱) | BSD 3-Clause | `third-party/angle-LICENSE.txt` |
| AndroidX·Kotlin 바인딩(Xamarin.AndroidX.* 등, Android 앱) | MIT(바인딩) + Apache-2.0(원 라이브러리) | 아래 13항 |
| DejaVu Sans Mono 2.37 (글꼴) | Bitstream Vera 라이선스 + DejaVu 변경분 퍼블릭 도메인 | `ProxmoxClient.UI/Assets/TerminalFonts/DejaVu-LICENSE.txt` |
| Noto Sans Math · Noto Sans Symbols · Noto Sans Symbols 2 · Noto Emoji (글꼴) | SIL OFL 1.1 | `ProxmoxClient.UI/Assets/TerminalFonts/Noto-OFL.txt` |
| Noto Sans Mono CJK KR 2.004 (글꼴) | SIL OFL 1.1 | `ProxmoxClient.UI/Assets/CjkFonts/NotoCJK-LICENSE.txt` |
| Symbols Nerd Font Mono 3.4.0 (글꼴) | MIT (+ 포함 아이콘 묶음별 라이선스) | `ProxmoxClient.UI/Assets/TerminalFonts/NerdFonts-LICENSE.txt` |

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

## 4. Net.Codecrete.QrCodeGenerator 3.2.1

- 저작권: (c) 2018 Manuel Bleichenbacher (Project Nayuki QR Code generator 의 C# 이식)
- 라이선스: MIT
- 출처: https://github.com/manuelbl/QrCodeGenerator
- 포함 형태: NuGet 패키지 참조(`Net.Codecrete.QrCodeGenerator.dll` 배포) — 2단계 인증(TOTP) 등록 QR 코드

---

## 5. IronRDP (Devolutions.IronRdp 2025.12.4)

- 저작권: (c) Devolutions Inc. 및 IronRDP 기여자
- 라이선스: MIT 또는 Apache License 2.0 중 선택(이 배포는 MIT 조건을 따른다)
- 출처: https://github.com/Devolutions/IronRDP (패키지 커밋 632ad86)
- 포함 형태: NuGet 패키지 참조 — `Devolutions.IronRdp.dll`(관리 바인딩)과
  `runtimes/win-x64/native/DevolutionsIronRdp.dll`(Rust 네이티브 코어) 배포. RDP 콘솔(VM 디스플레이 rdp)에 쓴다.

---

## 6. Vortice.Windows 3.8.3 (Vortice.Direct2D1 · Direct3D11 · DXGI · DirectX · Mathematics) · SharpGen.Runtime 2.4.2-beta

- 저작권: (c) Amer Koleci 및 Vortice.Windows 기여자 / (c) Andrew Ventura·Jeremy Koritzinsky(SharpGen)
- 라이선스: MIT
- 출처: https://github.com/amerkoleci/Vortice.Windows , https://github.com/SharpGenTools/SharpGenTools
- 포함 형태: NuGet 패키지 참조(관리 DLL 배포) — 콘솔 화면을 Direct3D 11·Direct2D 로 바로 그리는 데 쓴다
  (`Rendering/DirectScreenRenderer.cs`). DirectX 런타임 자체는 Windows 에 들어 있는 것을 쓴다.

---

## 7. FluentFTP 55.1.0

- 저작권: (c) Robin Rodricks 및 FluentFTP 기여자
- 라이선스: MIT
- 출처: https://github.com/robinrodricks/FluentFTP
- 포함 형태: NuGet 패키지 참조(`FluentFTP.dll` 배포) — 게스트 파일 창의 FTP·FTPS 연결(`Core/Files/FtpFileSystem.cs`).

---

## 8. Pretendard 1.3.9 (글꼴)

- 저작권: (c) 2021 Kil Hyung-jin (Pretendard), 바탕 글꼴 Inter (c) The Inter Project Authors, Source Han Sans (c) Adobe
- 라이선스: SIL Open Font License 1.1
- 출처: https://github.com/orioncactus/pretendard
- 포함 형태: 글꼴 파일을 앱에 내장(`ProxmoxClient.UI/Assets/Fonts/Pretendard-*.otf`, 라이선스 `Pretendard-LICENSE.txt`) —
  Avalonia 앱(데스크톱·Android)의 기본 글꼴(한글·라틴).

---

## 9. JetBrains Mono (글꼴)

- 저작권: (c) 2020 JetBrains s.r.o.
- 라이선스: SIL Open Font License 1.1 (배포 패키지에 따라 Apache-2.0 표기 — 둘 다 내장 배포 허용)
- 출처: https://github.com/JetBrains/JetBrainsMono
- 포함 형태: 글꼴 파일을 앱에 내장(`ProxmoxClient.UI/Assets/Fonts/JetBrainsMono-*.ttf`, 라이선스
  `JetBrainsMono-LICENSE.txt`) — 터미널(콘솔)과 로그 등 고정폭 글자.

---

## 10. SSH.NET 2026.0.0 · BouncyCastle.Cryptography 2.7.0

- 저작권: Copyright (c) Renci 2010-2026 (SSH.NET) /
  Copyright (c) 2000-2026 The Legion of the Bouncy Castle Inc. (BouncyCastle, SSH.NET 이 쓰는 암호 라이브러리)
- 라이선스: MIT
- 출처: https://github.com/sshnet/SSH.NET , https://www.bouncycastle.org/csharp/
- 포함 형태: NuGet 패키지 참조(`Renci.SshNet.dll`·`BouncyCastle.Cryptography.dll` 배포) —
  게스트 파일 창의 SFTP 연결과 SSH 키 설정(`Core/Files/SftpFileSystem.cs`·`SshKeySetup.cs`).
  BouncyCastle 라이선스 원문은 `third-party/bouncycastle-LICENSE.md`.

---

## 11. Avalonia 12.1.5

- 저작권: Copyright 2013-2026 (c) The AvaloniaUI Project
- 라이선스: MIT
- 출처: https://github.com/AvaloniaUI/Avalonia
- 포함 형태: NuGet 패키지 참조 — Avalonia 앱(데스크톱·Android·iOS)의 화면 프레임워크.
  `Avalonia`·`Avalonia.Themes.Fluent`·`Avalonia.Desktop`(Win32·X11·Native·FreeDesktop)·`Avalonia.Android`·
  `Avalonia.iOS`·`Avalonia.Skia`·`Avalonia.HarfBuzz`·`Avalonia.Remote.Protocol`, 표 컨트롤
  `Avalonia.Controls.DataGrid` 12.1.2(MIT, https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid).
  함께 따라오는 MIT 구성요소: MicroCom.Runtime 0.11.6 (c) Nikita Tsukanov,
  Tmds.DBus.Protocol 0.94.1 (c) Tom Deseyn(Linux 데스크톱).
- Windows 데스크톱은 ANGLE(`Avalonia.Angle.Windows.Natives`, (c) The ANGLE Project Authors, BSD 3-Clause)을
  함께 배포한다 — 원문 `third-party/angle-LICENSE.txt`.

---

## 12. SkiaSharp 3.119.4 · HarfBuzzSharp 8.3.1.3

- 저작권: Copyright (c) 2015-2016 Xamarin, Inc. / Copyright (c) 2017-2018 Microsoft Corporation
- 라이선스: MIT (관리 바인딩) — 원문 `third-party/skiasharp-LICENSE.txt`
- 출처: https://github.com/mono/SkiaSharp
- 포함 형태: NuGet 패키지 참조 — `SkiaSharp.dll`·`HarfBuzzSharp.dll` 과 플랫폼별 네이티브 라이브러리
  (`libSkiaSharp`·`libHarfBuzzSharp`). 화면 그리기와 글자 모양 잡기(터미널·VNC 화면 포함).

네이티브 라이브러리는 Skia(BSD 3-Clause, Google), HarfBuzz(Old MIT), FreeType(FreeType License),
libpng, libjpeg-turbo, libwebp, expat, zlib, ICU, ANGLE 등을 포함합니다. 패키지에 동봉된 전체 고지 원문을
`third-party/skiasharp-harfbuzzsharp-THIRD-PARTY-NOTICES.txt` 에 그대로 두었습니다.

FreeType License 가 요구하는 고지: Portions of this software are copyright (c) The FreeType Project
(www.freetype.org). All rights reserved.

---

## 13. Android 지원 라이브러리 (Android 앱만)

- 포함 형태: Avalonia.Android·Xamarin.AndroidX.Core.SplashScreen 1.2.0.2 가 끌어오는 NuGet 바인딩
  (`Xamarin.AndroidX.*`, `Xamarin.Kotlin.StdLib`, `Xamarin.KotlinX.Coroutines.*`,
  `Xamarin.KotlinX.Serialization.Core`, `Xamarin.Google.Guava.ListenableFuture`, `Xamarin.JSpecify`,
  `Xamarin.Jetbrains.Annotations` 등 약 60개)
- 라이선스: .NET 바인딩은 MIT ((c) Microsoft Corporation), 감싼 원 라이브러리(AndroidX, Kotlin, kotlinx, Guava,
  JSpecify, JetBrains annotations)는 Apache License 2.0
- 출처: https://github.com/dotnet/android-libraries , https://developer.android.com/jetpack/androidx
- Apache License 2.0 전문: https://www.apache.org/licenses/LICENSE-2.0 (`third-party/ironrdp-LICENSE-APACHE.txt` 와 같은 내용)

Android·iOS 앱은 .NET 런타임(MIT, (c) .NET Foundation and Contributors)을 앱 안에 담아 배포합니다.

---

## 14. DejaVu Sans Mono 2.37 (글꼴)

- 저작권: Copyright (c) 2003 by Bitstream, Inc. All Rights Reserved. Bitstream Vera is a trademark of Bitstream, Inc.
  DejaVu changes are in public domain. Arev 글리프는 Copyright (c) 2006 by Tavmjong Bah.
- 라이선스: Bitstream Vera Fonts 라이선스(+ Arev Fonts 라이선스), DejaVu 변경분은 퍼블릭 도메인
- 출처: https://dejavu-fonts.github.io
- 포함 형태: 글꼴 파일을 앱에 내장(`ProxmoxClient.UI/Assets/TerminalFonts/DejaVuSansMono.ttf`·`DejaVuSansMono-Bold.ttf`,
  라이선스 `DejaVu-LICENSE.txt`) — 터미널 대체 글꼴(상자 그리기·기호 등 JetBrains Mono 에 없는 글자).

---

## 15. Noto Sans Math · Noto Sans Symbols · Noto Sans Symbols 2 · Noto Emoji (글꼴)

- 저작권: Copyright 2022 The Noto Project Authors (https://github.com/notofonts/symbols),
  Copyright 2022 Google LLC (Noto Sans Math), Copyright 2013 Google LLC (Noto Emoji)
- 라이선스: SIL Open Font License 1.1
- 출처: https://github.com/notofonts/math , https://github.com/notofonts/symbols ,
  https://github.com/google/fonts/tree/main/ofl/notoemoji
- 포함 형태: 글꼴 파일을 앱에 내장(`ProxmoxClient.UI/Assets/TerminalFonts/NotoSansMath-Regular.ttf`·
  `NotoSansSymbols-Regular.ttf`·`NotoSansSymbols2-Regular.ttf`·`NotoEmoji-Regular.ttf`, 라이선스 `Noto-OFL.txt`) —
  터미널 대체 글꼴(수학 기호·각종 기호·흑백 이모지).

---

## 16. Noto Sans Mono CJK KR 2.004 (글꼴)

- 저작권: (c) 2014-2021 Adobe (http://www.adobe.com/), Noto 는 Google LLC 의 상표
- 라이선스: SIL Open Font License 1.1
- 출처: https://github.com/notofonts/noto-cjk
- 포함 형태: 글꼴 파일을 앱에 내장(`ProxmoxClient.UI/Assets/CjkFonts/NotoSansMonoCJKkr-Regular.otf`,
  라이선스 `NotoCJK-LICENSE.txt`) — 터미널의 한자·가나·중국어 등 넓은 글자.

---

## 17. Symbols Nerd Font Mono 3.4.0 (글꼴)

- 저작권: Copyright (c) 2014 Ryan L McIntyre (Nerd Fonts)
- 라이선스: MIT (Nerd Fonts) — 원문 `ProxmoxClient.UI/Assets/TerminalFonts/NerdFonts-LICENSE.txt`
- 출처: https://github.com/ryanoasis/nerd-fonts
- 포함 형태: 글꼴 파일을 앱에 내장(`ProxmoxClient.UI/Assets/TerminalFonts/SymbolsNerdFontMono-Regular.ttf`) —
  터미널 프롬프트·파일 목록 도구가 쓰는 아이콘 글자(Private Use Area).

이 글꼴에 모인 아이콘 묶음은 각자의 원 라이선스를 따릅니다 — Font Awesome·Weather Icons·Pomicons(SIL OFL 1.1),
Material Design Icons(Apache-2.0), Octicons·Powerline Symbols·Devicons·Seti-UI(MIT), Codicons(CC BY 4.0,
(c) Microsoft Corporation), Font Logos(Unlicense) 등. 묶음별 저작권과 라이선스 목록은
https://github.com/ryanoasis/nerd-fonts/blob/master/license-audit.md 에 있습니다.

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

.NET 및 WPF 런타임은 Windows(WPF) 배포물에 포함되지 않습니다(프레임워크 종속 배포). Android·iOS 앱은 13항 참고.
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

# WPF → Avalonia 이식 가이드

WPF 앱(`ProxmoxClient.App`)의 화면을 `ProxmoxClient.UI` 로 옮길 때 따르는 규칙. 공용 틀(표·입력 폼·탭 탐색)은
WPF 와 **같은 클래스 이름·같은 생성자**로 이미 옮겨 두었으므로, 대부분의 파일은 아래 치환만으로 옮겨진다.

## 위치와 네임스페이스

| WPF | Avalonia |
|---|---|
| `ProxmoxClient.App/Views/<영역>/X.cs` | `ProxmoxClient.UI/Views/<영역>/X.cs` |
| `namespace ProxmoxClient.App.Views.<영역>` | `namespace ProxmoxClient.UI.Views.<영역>` |
| `ProxmoxClient.App.Views.Shared` | `ProxmoxClient.UI.Views.Shared` |
| `ProxmoxClient.App.Localization` (Loc.T) | `ProxmoxClient.UI.Localization` (Loc.T 그대로 동작, WPF 문구 2,805개가 `Localization/App.resx` 에 있음) |
| `ProxmoxClient.App` (ByteFormatter · ComboChoices · GraphSeries · GraphAxis · GraphValueUnit) | `ProxmoxClient.UI` (같은 이름) |
| `ProxmoxClient.App.Controls.GraphLine` | `ProxmoxClient.UI.Views.Controls.GraphLine` (같은 속성) |

## 형식·API 치환

| WPF | Avalonia |
|---|---|
| `using System.Windows*` | 지우고 `using Avalonia.Controls;` 등 |
| `Window? owner` (작업·열기 콜백 인자) | `Control? owner` |
| `UIElement` / `FrameworkElement` | `Control` |
| `Visibility.Visible/Collapsed` | `IsVisible = true/false` |
| `new FormDialog(t, fields, v, target){Owner=o}.ShowDialog()` + `.Result` | `await FormDialog.ShowAsync(t, fields, v, target)` (취소면 null) |
| `ThemedMessageBox.Show(..., YesNo, ...) == Yes` | `await Ask.ConfirmAsync(text, title)` |
| `ThemedMessageBox.Show(... OK ...)` | `await Ask.InfoAsync(text, title)` / `Ask.ErrorAsync(text)` |
| `Clipboard.SetText(x)` | `await ClipboardHelper.SetTextAsync(x)` (`ProxmoxClient.UI.Views`) |
| `OpenFileDialog` / `SaveFileDialog` | `TopLevel.GetTopLevel(control)!.StorageProvider.OpenFilePickerAsync / SaveFilePickerAsync` |
| `Dispatcher.Invoke/BeginInvoke` | `Avalonia.Threading.Dispatcher.UIThread.Post / InvokeAsync` |
| `TableWindow.ShowModal(owner, title, content)` · `TextViewWindow.ShowModal(...)` | 같은 이름(새 페이지로 연다) |
| `TaskLogViewer.Show(api, task, owner)` | 같은 이름 |
| `new NavWindow(...).Show()` | 같은 이름(앱 화면 스택에 연다) |
| `DataGrid` | `TableTab`(표) 또는 `ItemsControl` + 카드 |
| `.xaml` 화면 | `.axaml`(Avalonia 문법) 또는 코드로 만든 `UserControl`. 스타일 클래스: `card` `title` `subtle` `section` `label` `row` `icon` `chip` `usage` `accent` `danger` |

공용 틀: `TableTab` `TableColumn` `TableAction` `TableRow` `TableFormats` `FormField` `FormFieldKind` `FormDialog`
`ActionHelpers` `ActionBar` `ActionRunner` `Ask` `NavTab` `NavTabList` `NavWindow` `SubTab` `SubTabsView`
`TextEditTab` `TableWindow` `TextViewWindow` `TaskLogViewer` — 모두 `ProxmoxClient.UI.Views.Shared`.

## 반드시 지킬 것

1. **서버 상태를 바꾸는 작업은 확인 없이 실행하지 않는다.** 통신 계층(`WriteConfirmation`)이 확인 없는 쓰기를 막으므로
   보통은 따로 할 일이 없다. 절대로 `WriteGuard` 를 바꾸거나 우회하지 말 것.
   `WriteConfirmation.Begin(이름, confirmed: true)` 는 바로 앞에서 `Ask.ConfirmAsync` 가 true 를 돌려준 경우에만 쓴다.
2. 데스크톱·모바일 모두에서 동작해야 한다(같은 코드). 화면 폭이 412px 여도 잘리지 않게(가로 고정 폭 피하기, WrapPanel 사용).
3. `ProxmoxClient.Core`, `Views/Shared`, `Services`, `App.axaml`, 다른 영역 폴더는 고치지 않는다.
   부족한 공용 기능은 자기 영역 안에 작은 도우미로 만들고 보고에 적는다.
4. 새 문구는 자기 영역 파일 `Localization/<영역>.resx`(한국어) + `<영역>.en.resx`(영어)에 넣는다(다른 resx 는 고치지 않음).
   WPF 에서 쓰던 키는 이미 있으므로 그대로 쓴다.
5. 진입점·검증 등록은 `Views/<영역>/<영역>Module.cs` 의 `[ModuleInitializer]` 에서
   `FeatureRegistry`(실제 진입점) 또는 `PreviewRegistry.Add("<영역>", new PreviewTab(...))`(검증용)로 한다.

## 검증(전환마다)

```bash
export PATH=$HOME/.dotnet:$PATH
dotnet build ProxmoxClient.Desktop -c Debug          # 오류 0, 새 경고 0
python3 -I tools/FakePve/fake_pve.py <포트> &         # 가짜 PVE 서버(포트는 작업마다 다르게)
dotnet run --project tools/UiSnapshot -- --port <포트> --out <폴더> --mode desktop --target preview:<영역>
dotnet run --project tools/UiSnapshot -- --port <포트> --out <폴더> --mode mobile  --target preview:<영역>
```

- 출력의 `FAIL` · `EXCEPTION` 줄이 없어야 한다. 캡처 몇 장은 직접 열어 보고(표가 비었거나 잘리지 않았는지) 확인한다.
- 서버 로그의 `UNHANDLED GET ...` 은 그 화면이 부르는 API 의 예시 응답이 없다는 뜻 — `tools/FakePve/routes/<영역>.py` 에
  예시 데이터를 추가해 표에 내용이 보이게 한다(README 참고).

## 화면 모양은 WPF 와 같게(필수)

- 테마: `Themes/WpfTheme.axaml` — WPF DarkTheme 과 같은 색·이름(`BrushBg` `BrushPanel` `BrushControl` `BrushBorder`
  `BrushAccent` `BrushText` `BrushDim` `BrushOk` `BrushWarn` `BrushErr` `BrushSelection` `BrushHover`).
- WPF `Style="{StaticResource X}"` → `Classes="X"` (같은 이름): `AccentButton` `DangerButton` `DialogButton`
  `DialogPrimaryButton` `KeyToggle` `FormPanel` `DialogFooter` `SummaryLabel` `FormSection` `SectionHeader`
  `FieldLabel` `DialogStatus` `NavListBox` `NavListBoxItem` `SubTabs`(ListBox) / `SubTabItem` `PaneSplitterH` `PaneSplitterV`.
- `ic:IconAssist.Icon` · `ic:IconAssist.Dropdown` · `ic:IconLabel` · `ic:FormRow` → `xmlns:c="using:ProxmoxClient.UI.Views.Controls"` 의 같은 이름.
- WPF 의 `DataGrid` 는 Avalonia `DataGrid`(같은 열·같은 폭·같은 머리글)로 옮긴다 — 카드 목록으로 바꾸지 않는다.
  모바일에서만 카드가 필요하면 `PlatformInfo.IsDesktop` 으로 나눈다(데스크톱은 반드시 WPF 와 같은 표).
- 배치(Grid 행·열, 여백, 버튼 순서·이름·아이콘)는 WPF XAML 그대로. `DataTrigger` 는 `IsVisible="{Binding ...}"` 등 바인딩으로.
- WPF 에서 하던 규칙(버튼 사용 조건, 확인 문구, 상태별 표시)은 그대로 옮긴다 — 임의로 바꾸지 않는다.

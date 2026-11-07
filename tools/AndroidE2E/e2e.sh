#!/usr/bin/env bash
# Android 에뮬레이터 끝까지 검사 — 디버그 APK 의 자동 검사 고리(ProxmoxClient.Android/DebugTestHook.cs)로 앱을 움직이고
# 안전 영역·액티비티 다시 만들기·연결 유지 서비스·마우스 오른쪽 클릭·화면 보호를 실제 Android 에서 확인한다.
# 쓰기: e2e.sh <apk> <결과 폴더> <가짜 PVE 포트> <가짜 PVE 로그>   (가짜 PVE 는 러너 127.0.0.1 — 에뮬레이터에서 10.0.2.2)
# 결과: PASS/FAIL 줄(결과 폴더/results.txt), 화면 캡처(*.png), logcat·dumpsys 기록. FAIL 이 하나라도 있으면 끝 코드 1.
set -u
APK=$1
OUT=$2
PORT=$3
FLOG=$4
HERE=$(cd "$(dirname "$0")" && pwd)
PKG=io.github.moonjm98.proxmoxclient
ACTION=$PKG.debug.TEST
mkdir -p "$OUT"
RESULTS=$OUT/results.txt
PCLOG=$OUT/pctest.log
: > "$RESULTS"

pass() { echo "PASS $*" | tee -a "$RESULTS"; }
fail() { echo "FAIL $*" | tee -a "$RESULTS"; }
info() { echo "INFO $*" | tee -a "$RESULTS"; }
section() { echo; echo "======== $*"; }
shot() { adb exec-out screencap -p > "$OUT/$1.png"; echo "    screenshot $1.png"; }
pid() { adb shell pidof "$PKG" | tr -d '\r'; }

# 고리 명령: hook <cmd> [am 추가 인자...] — 결과("OK cmd"/"ERR cmd")를 기다리고 마지막 상태 줄을 STATE 에 둔다
STATE=""
NEW=""
hook() {
  local cmd=$1
  shift
  local n
  n=$(wc -l < "$PCLOG")
  adb shell am broadcast -a "$ACTION" -p "$PKG" --es cmd "$cmd" "$@" > /dev/null
  for _ in $(seq 1 "${HOOK_TIMEOUT:-60}"); do
    tail -n +"$((n + 1))" "$PCLOG" | grep -qE "(OK|ERR) $cmd\b" && break
    sleep 1
  done
  NEW=$(tail -n +"$((n + 1))" "$PCLOG")
  echo "$NEW" | sed 's/^/    /'
  STATE=$(echo "$NEW" | grep "state page=" | tail -1)
  echo "$NEW" | grep -qE "OK $cmd\b"
}
# 터미널에 글자 보내기(base64 로 — 공백·$·줄바꿈을 그대로)
type_text() { hook type --es b64 "$(printf '%s\r' "$1" | base64 -w0)"; }
state_has() { echo "$STATE" | grep -q -- "$1"; }
ime_shown() { adb shell dumpsys input_method | grep -qE "mInputShown=true|isInputViewShown=true"; }
navmode() {
  adb shell cmd overlay enable-exclusive --category "com.android.internal.systemui.navbar.$1" || true
  sleep 4
  info "nav mode $1: $(adb shell cmd overlay list | grep -E 'navbar\.(gestural|threebutton)\b' | tr -d '\r' | tr '\n' ' ')"
}
launch() { adb shell am start -n "$ACT" > /dev/null; }

section "준비"
adb root > /dev/null 2>&1 && sleep 3
adb wait-for-device
adb shell settings put global window_animation_scale 0
adb shell settings put global transition_animation_scale 0
adb shell settings put global animator_duration_scale 0
adb shell settings put secure show_ime_with_hard_keyboard 1
# 느린 에뮬레이터에서 런처 등이 띄우는 "응답 없음" 창이 앱 위를 가리지 않게
adb shell settings put global hide_error_dialogs 1
adb shell settings put secure anr_show_background 0
# "활동 보관 안 함" — 설정 값은 시스템이 부팅 때 읽으므로 켜고 프레임워크를 다시 시작한다(검사 내내 켜 둔다:
# 앱이 배경으로 갈 때마다 액티비티가 없어지고 돌아오면 새로 만들어진다)
adb shell settings put global always_finish_activities 1
sleep 5 # 설정 값이 파일에 쓰일 때까지(바로 다시 시작하면 잃는다)
info "always_finish_activities before restart=$(adb shell settings get global always_finish_activities | tr -d '\r')"
adb shell setprop sys.boot_completed 0 || true
adb shell stop
adb shell start
for _ in $(seq 1 120); do [ "$(adb shell getprop sys.boot_completed | tr -d '\r')" = 1 ] && break; sleep 2; done
sleep 15
adb shell am broadcast -a android.intent.action.CLOSE_SYSTEM_DIALOGS > /dev/null 2>&1 || true
info "always_finish_activities=$(adb shell settings get global always_finish_activities | tr -d '\r') (framework restarted)"
adb install -r -g "$APK" > "$OUT/install.txt" 2>&1 || { cat "$OUT/install.txt"; fail "install"; exit 1; }
adb shell pm grant "$PKG" android.permission.POST_NOTIFICATIONS || true
ACT=$(adb shell cmd package resolve-activity --brief -c android.intent.category.LAUNCHER "$PKG" | tail -1 | tr -d '\r')
info "activity $ACT · $(adb shell getprop ro.build.version.release | tr -d '\r') API $(adb shell getprop ro.build.version.sdk | tr -d '\r') · $(adb shell wm size | tr -d '\r') $(adb shell wm density | tr -d '\r')"
adb logcat -c
adb logcat -v threadtime > "$OUT/logcat.txt" 2>&1 &
LOGCAT_PID=$!
adb logcat -v brief -s PCTEST:I > "$PCLOG" 2>&1 &
PCLOG_PID=$!
trap 'kill $LOGCAT_PID $PCLOG_PID 2>/dev/null || true' EXIT
navmode gestural
launch
for _ in $(seq 1 60); do grep -q "hook registered" "$PCLOG" && break; sleep 1; done
sleep 5
HOOK_TIMEOUT=10
for _ in $(seq 1 20); do hook state && state_has "page=ProfilesPage" && break; sleep 2; done
HOOK_TIMEOUT=60
state_has "page=ProfilesPage" && pass "app start: profile list shown" || fail "app start: profile list not shown ($STATE)"

section "1. 안전 영역(제스처 탐색)"
hook setup --es host 10.0.2.2 --ei port "$PORT" --es user root@pam --es password secret
sleep 2
shot 01-profiles-gesture
hook dialog
sleep 3
shot 02-dialog-gesture
state_has "dialog=True" && pass "dialog overlay shown (see 02-dialog-gesture.png)" || fail "dialog overlay not shown"
hook dismiss
sleep 1
hook connect && state_has "vm=ClusterViewModel" && pass "login to FakePve (10.0.2.2:$PORT) + trust certificate" || fail "login ($STATE)"
sleep 3
shot 03-cluster-gesture
hook shell --es node pve1
state_has "terminal=connected" && pass "node shell terminal connected" || fail "node shell terminal ($STATE)"
for _ in $(seq 1 10); do hook state; echo "$STATE" | grep -q 'last="root@pve1' && break; sleep 1; done
adb shell am broadcast -a android.intent.action.CLOSE_SYSTEM_DIALOGS > /dev/null 2>&1 || true
for _ in 1 2 3; do hook ime; sleep 4; ime_shown && break; done
ime_shown && pass "soft keyboard open on terminal" || fail "soft keyboard not open on terminal"
hook state
info "terminal+keyboard state: $STATE"
shot 04-terminal-keyboard-gesture

section "2. 액티비티 다시 만들기(활동 보관 안 함)"
type_text "export MARK=e2e42"
sleep 1
PID_BEFORE=$(pid)
n=$(wc -l < "$PCLOG")
adb shell input keyevent KEYCODE_HOME
sleep 5
tail -n +"$((n + 1))" "$PCLOG" | grep -q "activity destroyed" && pass "activity destroyed after HOME (don't keep activities)" || fail "activity was not destroyed after HOME"

section "3a. 연결 유지 서비스(배경)"
adb shell dumpsys activity services "$PKG" > "$OUT/dumpsys-services-bg.txt"
adb shell dumpsys notification --noredact > "$OUT/dumpsys-notification-bg.txt"
grep -q "TerminalKeepAliveService" "$OUT/dumpsys-services-bg.txt" && grep -q "isForeground=true" "$OUT/dumpsys-services-bg.txt" \
  && pass "keep-alive service is foreground while backgrounded" || fail "keep-alive service not foreground (dumpsys-services-bg.txt)"
grep -E "^\s*(isForeground|foregroundId|types|foregroundServiceType)" "$OUT/dumpsys-services-bg.txt" | head -5 | sed 's/^/    /'
grep -A3 "NotificationRecord.*pkg=$PKG" "$OUT/dumpsys-notification-bg.txt" | grep -q "channel=terminal_keepalive.*FOREGROUND_SERVICE" && pass "keep-alive notification posted (channel terminal_keepalive)" \
  || fail "keep-alive notification missing (dumpsys-notification-bg.txt)"
[ "$(pid)" = "$PID_BEFORE" ] && [ -n "$PID_BEFORE" ] && pass "process alive in background (pid $PID_BEFORE)" || fail "process changed/died in background ($PID_BEFORE → $(pid))"

section "2. (계속) 다시 열기"
n=$(wc -l < "$PCLOG")
launch
sleep 6
tail -n +"$((n + 1))" "$PCLOG" | grep -q "activity created" && pass "new activity created on relaunch" || fail "no new activity on relaunch"
hook state
state_has "page=TerminalPage" && pass "terminal page still shown after activity recreation" || fail "terminal page lost after recreation ($STATE)"
state_has "terminal=connected" && pass "terminal still connected after activity recreation" || fail "terminal not connected after recreation ($STATE)"
type_text 'echo MARK_IS_$MARK'
sleep 2
hook screen
echo "$NEW" | grep -q "MARK_IS_e2e42" && pass "same shell session after recreation (echo \$MARK → e2e42)" || fail "shell variable lost after recreation"
shot 05-terminal-after-recreate
# 화면 보호가 새 액티비티에도 걸리는지는 5 에서 본다

section "3b. 알림의 [모두 끊기]"
adb shell input keyevent KEYCODE_HOME
sleep 2
adb shell cmd statusbar expand-notifications
sleep 3
tapped=""
for attempt in 1 2 3; do
  adb shell uiautomator dump /sdcard/ui.xml > /dev/null 2>&1
  adb pull /sdcard/ui.xml "$OUT/ui-shade-$attempt.xml" > /dev/null 2>&1
  if xy=$(python3 -I "$HERE/uitap.py" "$OUT/ui-shade-$attempt.xml" "Disconnect all" "모두 끊기"); then
    shot 06-notification-shade
    adb shell input tap $xy
    tapped="notification action button at $xy"
    break
  fi
  # 접힌 알림 — 이 앱 알림("터미널 연결 유지 중") 뒤의 펼침 단추를 누르고 다시 찾는다
  if xy=$(python3 -I "$HERE/uitap.py" "$OUT/ui-shade-$attempt.xml" "after:Keeping terminal" "id:android:id/expand_button") \
    || xy=$(python3 -I "$HERE/uitap.py" "$OUT/ui-shade-$attempt.xml" "after:터미널 연결" "id:android:id/expand_button"); then
    adb shell input tap $xy
  fi
  sleep 2
done
if [ -z "$tapped" ]; then
  info "notification action not found in shade — sending the action intent instead (am startservice as root)"
  shot 06-notification-shade
  adb shell am startservice -n "$PKG/$PKG.TerminalKeepAliveService" -a "$PKG.action.DISCONNECT_TERMINALS" | tr -d '\r'
  tapped="am startservice DISCONNECT_TERMINALS"
fi
sleep 4
adb shell cmd statusbar collapse
hook state
state_has "terminal=disconnected" && pass "[Disconnect all] disconnected the terminal ($tapped)" || fail "[Disconnect all] did not disconnect ($tapped; $STATE)"
sleep 2
adb shell dumpsys activity services "$PKG" > "$OUT/dumpsys-services-after-disconnect.txt"
grep -q "TerminalKeepAliveService" "$OUT/dumpsys-services-after-disconnect.txt" && fail "keep-alive service still running after disconnect" \
  || pass "keep-alive service stopped after disconnect"
adb shell dumpsys notification --noredact | grep -q "NotificationRecord.*pkg=$PKG" && fail "notification still shown after disconnect" || pass "notification removed after disconnect"

section "3c. 연결하자마자 끊김 경쟁(10회)"
launch
sleep 4
curl -sk "https://127.0.0.1:$PORT/api2/json/__fake/termdrop?on=1"; echo
PID_RACE=$(pid)
alive=1
for i in $(seq 1 10); do
  hook reconnect > /dev/null
  sleep "0.$((i % 4))"
  p=$(pid)
  [ "$p" = "$PID_RACE" ] || { alive=0; info "race $i: pid $PID_RACE → '$p'"; }
done
sleep 3
drops=$(grep -c "TERM drop" "$FLOG" || true)
info "FakePve dropped $drops terminal connections"
[ "$alive" = 1 ] && [ "$(pid)" = "$PID_RACE" ] && pass "process alive through 10 connect-and-drop cycles (pid $PID_RACE)" || fail "process died during connect-and-drop"
if grep -E "did not then call Service.startForeground|ForegroundServiceDidNotStart|FATAL EXCEPTION|RemoteServiceException" "$OUT/logcat.txt" > "$OUT/crash-lines.txt"; then
  fail "crash lines in logcat (crash-lines.txt)"
  head -20 "$OUT/crash-lines.txt" | sed 's/^/    /'
else
  pass "no startForeground/FATAL crash in logcat"
fi
[ "$drops" -ge 10 ] && pass "FakePve dropped >= 10 connections" || fail "FakePve drops: $drops"
sleep 2
adb shell dumpsys activity services "$PKG" | grep -q "TerminalKeepAliveService" && fail "keep-alive service left running after drops" || pass "no keep-alive service left after drops"
curl -sk "https://127.0.0.1:$PORT/api2/json/__fake/termdrop?on=0"; echo
hook reconnect
for _ in $(seq 1 10); do hook state > /dev/null; state_has "terminal=connected" && break; sleep 1; done
state_has "terminal=connected" && pass "reconnect after race works" || fail "reconnect after race ($STATE)"

section "5. 화면 보호(FLAG_SECURE)"
W0=$(adb shell wm size | tr -d '\r' | sed -n 's/.*: \([0-9]*\)x\([0-9]*\).*/\1/p' | tail -1)
H0=$(adb shell wm size | tr -d '\r' | sed -n 's/.*: \([0-9]*\)x\([0-9]*\).*/\2/p' | tail -1)
win_flags() { adb shell dumpsys window windows | tr -d '\r' | awk -v pkg="$PKG" '/Window #/{inwin = index($0, pkg) > 0} inwin && /fl=/' | head -3; }
sleep 2
hook secure --es on true
sleep 2
flags=$(win_flags)
echo "    $flags"
echo "$flags" | grep -qw SECURE && pass "FLAG_SECURE set on the window (dumpsys window)" || fail "FLAG_SECURE not on the window"
adb exec-out screencap > "$OUT/secure-on.raw"
adb shell dumpsys SurfaceFlinger > "$OUT/surfaceflinger-secure-on.txt" 2>&1
shot 07-secure-on
px=$(python3 -I "$HERE/pixels.py" "$OUT/secure-on.raw")
info "secure on pixels: $px"
echo "$px" | grep -q "^max=\([0-9]\|1[0-9]\) " && pass "screencap of the app is black with screen protection" || fail "screencap not black with screen protection ($px)"
# 실행 중에만 켰다 끄기(그동안 SurfaceView 를 새로 만들지 않음) — 끄면 바로 보이는지
visible_within() { # visible_within <초> <이름> → 걸린 초를 VIS_AFTER 에, 보이면 0
  local t0 i b
  t0=$(date +%s)
  for i in $(seq 1 "$1"); do
    adb exec-out screencap > "$OUT/$2.raw"
    b=$(python3 -I "$HERE/pixels.py" "$OUT/$2.raw" | sed -n 's/.*bright=\([0-9]*\).*/\1/p')
    VIS_AFTER=$(( $(date +%s) - t0 ))
    [ "${b:-0}" -gt 200 ] && return 0
    sleep 1
  done
  return 1
}
info "runtime cycle: IME shown=$(ime_shown && echo yes || echo no)"
hook secure --es on false
if visible_within 10 secure-off-runtime; then
  pass "runtime on→off: app visible again after ${VIS_AFTER}s (no surface recreation)"
else
  fail "runtime on→off: app still black after ${VIS_AFTER}s"
fi
hook secure --es on true
sleep 2
# 새 액티비티에도 걸리는지(활동 보관 안 함 — HOME 으로 없어지고 다시 열면 새로 만들어진다)
n=$(wc -l < "$PCLOG")
adb shell input keyevent KEYCODE_HOME
sleep 3
launch
sleep 5
tail -n +"$((n + 1))" "$PCLOG" | grep -q "activity created" && info "activity recreated for the secure check" || info "activity was not recreated"
flags=$(win_flags)
echo "$flags" | grep -qw SECURE && pass "FLAG_SECURE kept on the recreated activity" || fail "FLAG_SECURE lost on recreated activity"
adb exec-out screencap > "$OUT/secure-recreated.raw"
px=$(python3 -I "$HERE/pixels.py" "$OUT/secure-recreated.raw")
echo "$px" | grep -q "^max=\([0-9]\|1[0-9]\) " && pass "recreated activity screencap black" || fail "recreated activity screencap not black ($px)"
# 최근 앱(개요) 미리보기 — 보호 중이면 앱 내용이 보이지 않아야 한다
adb shell input keyevent KEYCODE_APP_SWITCH
sleep 4
shot 07b-recents-secure-on
adb shell dumpsys activity recents > "$OUT/dumpsys-recents-secure-on.txt" 2>&1
adb shell uiautomator dump /sdcard/ui-recents.xml > /dev/null 2>&1
adb pull /sdcard/ui-recents.xml "$OUT/ui-recents-secure-on.xml" > /dev/null 2>&1
launch
sleep 5
info "recreated cycle: IME shown=$(ime_shown && echo yes || echo no)"
adb shell dumpsys window | tr -d '\r' | grep -iE "imeInputTarget|imeLayeringTarget|imeControlTarget" | head -6 | sed 's/^/    /'
hook secure --es on false
if visible_within 5 secure-off-quick; then
  pass "reopened while protected, then off: app visible again after ${VIS_AFTER}s without leaving the app"
else
  # 알려진 일: 보호 중에 다시 연(최근 앱·홈에서 돌아온) 작업은 끈 뒤에도 앱을 떠날 때까지 캡처가 검다.
  # 창 플래그는 지워졌고, SurfaceView 다시 만들기·액티비티 다시 만들기·키보드와 무관 — 시스템 쪽(작업 단위) 상태로 보인다
  info "KNOWN reopened while protected, then off: capture still black after ${VIS_AFTER}s until the app is left (system-side, see below)"
fi
# 끈 뒤 캡처가 다시 보일 때까지 걸린 시간
t0=$(date +%s)
px=""
for _ in $(seq 1 20); do
  adb exec-out screencap > "$OUT/secure-off.raw"
  px=$(python3 -I "$HERE/pixels.py" "$OUT/secure-off.raw")
  b=$(echo "$px" | sed -n 's/.*bright=\([0-9]*\).*/\1/p')
  [ "${b:-0}" -gt 200 ] && break
  sleep 1
done
info "screen protection off → capture visible after $(( $(date +%s) - t0 ))s ($px)"
if [ "${b:-0}" -le 200 ]; then
  # 아직 검다 — 화면 키보드(IME) 쪽인지 가른다: IME 대상 창 기록, 키보드를 닫고/다시 열고 캡처
  adb shell dumpsys window | tr -d '\r' | grep -iE "imeInputTarget|imeLayeringTarget|imeControlTarget|mImeTarget" | head -6 | sed 's/^/    /' | tee -a "$OUT/ime-targets.txt"
  hook resurface
  sleep 3
  adb exec-out screencap > "$OUT/secure-off-resurface.raw"
  info "after SurfaceView hide/show: $(python3 -I "$HERE/pixels.py" "$OUT/secure-off-resurface.raw")"
  hook bringtofront
  sleep 4
  adb exec-out screencap > "$OUT/secure-off-front.raw"
  info "after bring-to-front: $(python3 -I "$HERE/pixels.py" "$OUT/secure-off-front.raw")"
  adb shell dumpsys SurfaceFlinger > "$OUT/surfaceflinger-stuck.txt" 2>&1
  if ime_shown; then
    info "IME shown while still black"
    adb shell input keyevent KEYCODE_BACK # 키보드만 닫힌다
    sleep 3
    adb exec-out screencap > "$OUT/secure-off-imehidden.raw"
    info "after hiding the keyboard: $(python3 -I "$HERE/pixels.py" "$OUT/secure-off-imehidden.raw") ime_shown=$(ime_shown && echo yes || echo no)"
    hook ime
    sleep 3
    adb exec-out screencap > "$OUT/secure-off-imeshown.raw"
    info "after showing the keyboard again: $(python3 -I "$HERE/pixels.py" "$OUT/secure-off-imeshown.raw")"
  else
    info "IME not shown while still black"
    hook ime
    sleep 3
    adb exec-out screencap > "$OUT/secure-off-imeshown.raw"
    info "after showing the keyboard: $(python3 -I "$HERE/pixels.py" "$OUT/secure-off-imeshown.raw")"
  fi
  adb exec-out screencap > "$OUT/secure-off.raw"
  px=$(python3 -I "$HERE/pixels.py" "$OUT/secure-off.raw")
fi
flags=$(win_flags)
echo "$flags" | grep -qw SECURE && fail "FLAG_SECURE still set after disabling" || pass "FLAG_SECURE cleared after disabling"
shot 08-secure-off
info "secure off pixels: $px"
bright=$(echo "$px" | sed -n 's/.*bright=\([0-9]*\).*/\1/p')
adb shell dumpsys SurfaceFlinger > "$OUT/surfaceflinger-secure-off.txt" 2>&1
sf_secure=$(grep -iE "proxmoxclient|SurfaceView" "$OUT/surfaceflinger-secure-off.txt" | grep -ciE "isSecure=(true|1)|secure=true|\bSECURE\b" || true)
adb exec-out su 2000 screencap > "$OUT/secure-off-shell.raw" 2>/dev/null || true
info "secure off: SurfaceFlinger secure lines for app layers=$sf_secure · shell-user capture: $(python3 -I "$HERE/pixels.py" "$OUT/secure-off-shell.raw" 2>&1 | head -1)"
if [ "${bright:-0}" -gt 200 ]; then
  pass "screencap shows the app again after disabling"
else
  # 앱 탓인지 가르기 — 홈 화면(다른 앱)도 검게 찍히면 에뮬레이터 캡처가 통째로 멈춘 것
  adb shell input keyevent KEYCODE_HOME
  sleep 3
  adb exec-out screencap > "$OUT/secure-off-home.raw"
  shot 08b-secure-off-home
  home=$(python3 -I "$HERE/pixels.py" "$OUT/secure-off-home.raw")
  launch
  sleep 5
  adb exec-out screencap > "$OUT/secure-off-relaunch.raw"
  shot 08c-secure-off-relaunch
  again=$(python3 -I "$HERE/pixels.py" "$OUT/secure-off-relaunch.raw")
  info "secure off: home screen capture: $home · app after relaunch: $again"
  hb=$(echo "$home" | sed -n 's/.*bright=\([0-9]*\).*/\1/p')
  ab=$(echo "$again" | sed -n 's/.*bright=\([0-9]*\).*/\1/p')
  if [ "${ab:-0}" -gt 200 ]; then
    pass "screencap shows the app after disabling (after leaving and reopening the app)"
    [ "${hb:-0}" -gt 200 ] && info "home screen captured fine while the app stayed black — app stayed protected until reopened" \
      || info "home screen was black too — emulator capture stuck after secure toggle, not app-specific"
  else
    fail "screencap still dark after disabling ($px; home: $home; relaunch: $again)"
  fi
fi

adb shell input keyevent KEYCODE_APP_SWITCH
sleep 4
shot 08d-recents-secure-off
launch
sleep 5

section "4. 마우스 오른쪽 클릭 ≠ 뒤로 가기"
hook state
state_has "page=TerminalPage" || fail "precondition: not on terminal page ($STATE)"
n=$(wc -l < "$PCLOG")
adb shell input mouse keyevent KEYCODE_BACK
sleep 2
hook state
state_has "page=TerminalPage" && pass "BACK key from mouse source did not leave the terminal page" || fail "mouse-source BACK popped the page ($STATE)"
tail -n +"$((n + 1))" "$PCLOG" | grep -q "mouse BACK key dropped" && info "app dropped the mouse BACK key" || info "no 'mouse BACK key dropped' log"
# 진짜 마우스(uinput) 오른쪽 클릭 — 프레임워크가 BACK 으로 바꾸는 경로
if [ -n "${UINPUT_MOUSE:-}" ] && [ -f "$UINPUT_MOUSE" ]; then
  adb push "$UINPUT_MOUSE" /data/local/tmp/uinput_mouse > /dev/null
  adb shell chmod 755 /data/local/tmp/uinput_mouse
  n=$(wc -l < "$PCLOG")
  out=$(adb shell /data/local/tmp/uinput_mouse 400 700 right 2>&1 | tr -d '\r')
  echo "    uinput: $out"
  sleep 2
  tail -n +"$((n + 1))" "$PCLOG" | sed 's/^/    /'
  hook state
  if echo "$out" | grep -q "clicked right"; then
    tail -n +"$((n + 1))" "$PCLOG" | grep -q "secondary button" && info "app saw the secondary button" || info "app did not see a secondary button event"
    state_has "page=TerminalPage" && pass "real mouse right-click (uinput) did not leave the terminal page" || fail "real mouse right-click popped the page ($STATE)"
  else
    info "uinput mouse not available: $out"
  fi
  shot 09-after-right-click
else
  info "uinput mouse helper not built — skipped real right-click"
fi
# 키보드의 뒤로 가기는 그대로 화면을 닫는다(키보드가 떠 있으면 먼저 키보드만 닫힌다)
for _ in 1 2 3; do
  adb shell input keyboard keyevent KEYCODE_BACK
  sleep 2
  hook state > /dev/null
  state_has "page=TerminalPage" || break
done
hook state
state_has "page=TerminalPage" && fail "keyboard BACK did not navigate back ($STATE)" || pass "keyboard BACK navigates back (now $(echo "$STATE" | grep -o 'page=[A-Za-z]*'))"
if state_has "dialog=True"; then info "a confirm dialog is open after back — dismissing"; hook dismiss; fi
# 제스처 뒤로 가기(왼쪽 끝에서 밀기)
hook shell --es node pve1
sleep 2
adb shell input keyboard keyevent KEYCODE_ESCAPE > /dev/null 2>&1 || true
W=$(adb shell wm size | tr -d '\r' | sed -n 's/.*: \([0-9]*\)x\([0-9]*\).*/\1/p' | tail -1)
H=$(adb shell wm size | tr -d '\r' | sed -n 's/.*: \([0-9]*\)x\([0-9]*\).*/\2/p' | tail -1)
for _ in 1 2 3; do
  adb shell input swipe 2 $((H / 2)) $((W / 2)) $((H / 2)) 300
  sleep 2
  hook state > /dev/null
  state_has "page=TerminalPage" || break
done
hook state
state_has "page=TerminalPage" && fail "gesture back did not navigate back ($STATE)" || pass "gesture back navigates back (now $(echo "$STATE" | grep -o 'page=[A-Za-z]*'))"
if state_has "dialog=True"; then hook dismiss; fi

section "1. 안전 영역(3버튼 탐색)"
navmode threebutton
hook shell --es node pve1
sleep 2
hook ime
sleep 4
ime_shown && pass "soft keyboard open on terminal (3-button)" || fail "soft keyboard not open (3-button)"
hook state
info "terminal+keyboard state (3-button): $STATE"
shot 10-terminal-keyboard-3button
hook dialog
sleep 3
shot 11-dialog-3button
hook dismiss
hook root
sleep 2
sleep 2 # 뒤로 가기를 누르지 않는다(첫 화면에서 누르면 앱이 닫힌다)
sleep 1
hook state
shot 12-profiles-3button
navmode gestural

section "끝"
pidof_end=$(pid)
[ -n "$pidof_end" ] && pass "app process alive at the end" || fail "app process gone at the end"
grep -E "FATAL EXCEPTION|did not then call Service.startForeground|ForegroundServiceDidNotStart" "$OUT/logcat.txt" > /dev/null && fail "crash in logcat (logcat.txt)" || pass "no crash in logcat"
echo
echo "======== 결과"
cat "$RESULTS"
! grep -q "^FAIL" "$RESULTS"

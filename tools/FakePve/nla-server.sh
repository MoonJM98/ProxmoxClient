#!/bin/sh
# NLA(CredSSP/NTLM) 를 요구하는 RDP 서버를 띄운다 — FreeRDP 그림자 서버(freerdp3-shadow-x11)가 Xvfb 화면을 보여 준다.
# xrdp 는 NLA 가 없어서 앱의 CredSSP 경로를 검사하려면 이것이 필요하다(Windows 게스트는 기본으로 NLA 를 요구한다).
# 쓰기: nla-server.sh <작업 폴더> [포트=33891] [화면=:47] [사용자=nlauser] [비밀번호=NlaTest!234]
# 필요한 패키지: freerdp3-shadow-x11 winpr3-utils xvfb openbox xterm
set -e
dir=$1
port=${2:-33891}
display=${3:-:47}
user=${4:-nlauser}
password=${5:-NlaTest!234}
mkdir -p "$dir"
# NTLM SAM 파일(사용자:::NT 해시:::) — 서버는 이 해시로 NTLM 응답을 확인한다
winpr-hash3 -u "$user" -p "$password" -f sam > "$dir/SAM"
Xvfb "$display" -screen 0 1280x800x24 > "$dir/xvfb.log" 2>&1 &
sleep 1
DISPLAY=$display openbox > "$dir/openbox.log" 2>&1 &
DISPLAY=$display xterm -geometry 100x30+40+40 -fa Monospace -fs 12 > "$dir/xterm.log" 2>&1 &
sleep 1
# /sec:nla — NLA 만 받는다(TLS·RDP 보안으로 내려가지 않는다). 인증서는 처음 실행할 때 서버가 만든다
DISPLAY=$display freerdp-shadow-cli3 /port:"$port" /bind-address:127.0.0.1 /sec:nla /sam-file:"$dir/SAM" \
    > "$dir/shadow.log" 2>&1 &
i=0
while [ $i -lt 50 ]; do
    if ss -ltn | grep -q "127.0.0.1:$port "; then echo "NLA RDP server on 127.0.0.1:$port"; exit 0; fi
    sleep 0.2
    i=$((i + 1))
done
echo "NLA RDP server did not start" >&2
cat "$dir/shadow.log" >&2
exit 1

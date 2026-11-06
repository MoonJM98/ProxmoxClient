#!/bin/bash
# pmxcfs 는 호스트 이름이 127.x 가 아닌 주소로 풀려야 뜬다 — 컨테이너 주소로 /etc/hosts 를 쓴다
set -eu
ip=$(ip -4 -o addr show scope global | awk '{print $4}' | cut -d/ -f1 | head -1)
[ -n "$ip" ] || ip=127.0.1.1
echo pve > /etc/hostname
hostname pve || true
printf '127.0.0.1 localhost\n%s pve.e2e.local pve\n::1 localhost ip6-localhost ip6-loopback\n' "$ip" > /etc/hosts
# VM·CT 설정 검사용 다리(밖으로 나가지 않음)
ip link show vmbr0 >/dev/null 2>&1 || { ip link add vmbr0 type bridge; ip link set vmbr0 up; }

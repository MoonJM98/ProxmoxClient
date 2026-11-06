#!/bin/bash
# 시험 대상 만들기(컨테이너 안에서 실행): root 비밀번호, 저장소, VM 100(Alpine ISO, VGA std), CT 200(가능하면),
# TOTP 사용자 tfa@pve(알려진 비밀), API 토큰 root@pam!e2e. 결과 값은 /root/e2e.env 에 적는다.
# 사용: fixtures.sh <root 비밀번호> <TOTP base32 비밀>
set -uo pipefail
PASS="$1"
SECRET="$2"
out=/root/e2e.env
: > "$out"
log() { echo "[fixtures] $*"; }

echo "root:$PASS" | chpasswd

# 저장소: 컨테이너에는 LVM 이 없다 — local(디렉터리) 하나에 모든 내용
cat > /etc/pve/storage.cfg <<'EOF'
dir: local
	path /var/lib/vz
	content images,iso,vztmpl,backup,snippets,rootdir,import
	shared 0
EOF

# VM 100: 작은 디스크 + Alpine virt ISO 로 부팅(VGA 콘솔에 로그인 프롬프트). 시작은 앱이 한다
kvm=1
[ -w /dev/kvm ] || { kvm=0; log "WARNING: /dev/kvm 없음 — TCG(느림)"; }
qm create 100 --name e2e-vm --memory 256 --cores 1 --vga std --ostype l26 --kvm $kvm \
    --net0 virtio,bridge=vmbr0 --scsihw virtio-scsi-single --scsi0 local:1,format=qcow2 \
    --ide2 local:iso/alpine-virt.iso,media=cdrom --boot 'order=ide2;scsi0' \
    --description "e2e 시험 VM" || log "ERROR: qm create 100"
qm config 100

# CT 200: Alpine 템플릿을 받아(되면) 특권 컨테이너로. Docker 안이라 AppArmor 는 끈다
ct=0
pveam update >/dev/null 2>&1 || log "WARNING: pveam update 실패"
tmpl=$(pveam available --section system 2>/dev/null | awk '{print $2}' | grep '^alpine-.*_amd64' | sort -V | tail -1)
if [ -n "$tmpl" ] && pveam download local "$tmpl"; then
    if pct create 200 "local:vztmpl/$tmpl" --hostname e2e-ct --memory 128 --swap 0 --cores 1 \
        --rootfs local:1 --net0 name=eth0,bridge=vmbr0,ip=dhcp,type=veth --ostype alpine \
        --unprivileged 0 --features nesting=1 --password "$PASS"; then
        echo 'lxc.apparmor.profile: unconfined' >> /etc/pve/lxc/200.conf
        if pct start 200; then ct=1; else log "WARNING: pct start 200 실패"; pct start 200 --debug 2>&1 | tail -60; fi
        pct status 200 || true
    else
        log "WARNING: pct create 200 실패"
    fi
else
    log "WARNING: 템플릿 받기 실패($tmpl)"
fi
echo "PVE_CT=$ct" >> "$out"

# TOTP 사용자: pve 영역, 읽기 권한, 알려진 비밀(현재 코드로 등록)
pveum user add tfa@pve --password "$PASS" --comment "e2e totp" || log "ERROR: user add"
pveum acl modify / --users tfa@pve --roles PVEAuditor
code=$(python3 - "$SECRET" <<'PY'
import base64, hmac, hashlib, struct, sys, time
key = base64.b32decode(sys.argv[1].upper() + "=" * (-len(sys.argv[1]) % 8))
mac = hmac.new(key, struct.pack(">Q", int(time.time()) // 30), hashlib.sha1).digest()
o = mac[-1] & 15
print("%06d" % ((struct.unpack(">I", mac[o:o + 4])[0] & 0x7fffffff) % 1000000))
PY
)
pvesh create /access/tfa/tfa@pve --type totp --description e2e \
    --totp "otpauth://totp/e2e:tfa@pve?secret=$SECRET&issuer=e2e&algorithm=SHA1&digits=6&period=30" \
    --value "$code" --output-format json || log "ERROR: tfa add"

# API 토큰(권한 분리 없음 — root 권한 그대로)
token=$(pveum user token add root@pam e2e --privsep 0 --output-format json | jq -r '.value')
echo "PVE_TOKEN_ID=root@pam!e2e" >> "$out"
echo "PVE_TOKEN_SECRET=$token" >> "$out"
pveversion -v | head -5
log "done"
cat "$out"

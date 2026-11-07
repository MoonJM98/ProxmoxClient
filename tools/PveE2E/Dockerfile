# 실제 Proxmox VE(시험용) — 특권 Docker 컨테이너에서 systemd 를 PID 1 로 돌린다.
# 시험 전용: 비밀번호가 고정이고 자체 서명 인증서다. 운영에 쓰지 말 것.
# 참고: client-api/proxmox-docker (Apache-2.0) 의 방법(pmxcfs 는 fuse, 호스트 이름은 루프백이 아닌 주소로)
FROM debian:trixie-slim

ENV DEBIAN_FRONTEND=noninteractive LANG=C.UTF-8 LC_ALL=C.UTF-8 container=docker

# slim 이미지는 /usr/share/doc 를 지우는데 pve-manager 설치 스크립트가 거기서 파일을 읽는다
RUN rm -f /etc/dpkg/dpkg.cfg.d/docker \
 && apt-get update && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
 && curl -fsSL https://enterprise.proxmox.com/debian/proxmox-archive-keyring-trixie.gpg \
        -o /usr/share/keyrings/proxmox-archive-keyring.gpg \
 && echo "136673be77aba35dcce385b28737689ad64fd785a797e57897589aed08db6e45  /usr/share/keyrings/proxmox-archive-keyring.gpg" | sha256sum -c - \
 && printf 'Types: deb\nURIs: http://download.proxmox.com/debian/pve\nSuites: trixie\nComponents: pve-no-subscription\nSigned-By: /usr/share/keyrings/proxmox-archive-keyring.gpg\n' \
        > /etc/apt/sources.list.d/proxmox.sources

# 빌드 중에는 서비스를 띄우지 않는다
RUN printf '#!/bin/sh\nexit 101\n' > /usr/sbin/policy-rc.d && chmod +x /usr/sbin/policy-rc.d \
 && apt-get update \
 && apt-get install -y --no-install-recommends proxmox-ve iproute2 jq python3 e2fsprogs \
 && rm -rf /var/lib/apt/lists/* /var/cache/apt/archives/*

# 시험 VM 이 부팅할 Alpine virt ISO(VGA 콘솔에 로그인 프롬프트)
ARG ALPINE_ISO=https://dl-cdn.alpinelinux.org/alpine/v3.22/releases/x86_64/alpine-virt-3.22.2-x86_64.iso
RUN install -d /var/lib/vz/template/iso /var/lib/vz/template/cache \
 && curl -fsSL -o /var/lib/vz/template/iso/alpine-virt.iso "$ALPINE_ISO"

# 컨테이너 안에서 할 일이 없거나 부팅을 막는 단위는 끈다
RUN systemctl mask systemd-networkd-wait-online.service systemd-resolved.service systemd-timesyncd.service \
        chrony.service watchdog-mux.service rpcbind.service pve-ha-lrm.service pve-ha-crm.service \
        pvefw-logger.service spiceproxy.service postfix.service 2>/dev/null || true \
 && systemctl enable pve-cluster.service pvedaemon.service pveproxy.service pvestatd.service \
 && systemctl set-default multi-user.target

COPY pve-hostname.sh /usr/local/sbin/pve-hostname.sh
COPY pve-hostname.service /etc/systemd/system/pve-hostname.service
# lxcfs 는 컨테이너 안이면 뜨지 않는다(조건 비우기) — LXC 시작에 필요
COPY lxcfs-override.conf /etc/systemd/system/lxcfs.service.d/override.conf
RUN chmod +x /usr/local/sbin/pve-hostname.sh && systemctl enable pve-hostname.service \
 && rm -f /usr/sbin/policy-rc.d

EXPOSE 8006
STOPSIGNAL SIGRTMIN+3
ENTRYPOINT ["/lib/systemd/systemd"]

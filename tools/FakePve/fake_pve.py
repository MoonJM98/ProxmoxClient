import json, ssl, time, sys, re, os, glob, importlib.util, urllib.parse, subprocess
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
# 보안 키(webauthn@pam) 질문의 challenge(base64url)
WEBAUTHN_CHALLENGE='q83vEjRWeJA-_w'
now=int(time.time())
GUESTS=[
 dict(id='qemu/100',type='qemu',vmid=100,name='web-01',node='pve1',status='running',cpu=0.123,maxcpu=4,mem=2_400_000_000,maxmem=4_294_967_296,disk=0,maxdisk=34_359_738_368,netin=12_345_678_900,netout=2_345_678_900,uptime=86400*3+3600*5,template=0,tags='web;prod'),
 dict(id='qemu/101',type='qemu',vmid=101,name='db-01',node='pve1',status='running',cpu=0.47,maxcpu=8,mem=12_000_000_000,maxmem=17_179_869_184,disk=0,maxdisk=107_374_182_400,netin=5_000_000,netout=9_000_000,uptime=7200,template=0),
 dict(id='lxc/200',type='lxc',vmid=200,name='dns',node='pve2',status='running',cpu=0.01,maxcpu=1,mem=60_000_000,maxmem=536_870_912,disk=1_200_000_000,maxdisk=8_589_934_592,netin=100_000,netout=200_000,uptime=3600*30,template=0),
 dict(id='qemu/102',type='qemu',vmid=102,name='win11-test',node='pve2',status='stopped',cpu=0,maxcpu=4,mem=0,maxmem=8_589_934_592,disk=0,maxdisk=68_719_476_736,netin=0,netout=0,uptime=0,template=0),
 dict(id='qemu/9000',type='qemu',vmid=9000,name='debian-12-template',node='pve1',status='stopped',cpu=0,maxcpu=2,mem=0,maxmem=2_147_483_648,disk=0,maxdisk=10_737_418_240,netin=0,netout=0,uptime=0,template=1),
]
NODES=[dict(id='node/pve1',type='node',node='pve1',status='online',cpu=0.18,maxcpu=16,mem=40_000_000_000,maxmem=68_719_476_736,disk=20_000_000_000,maxdisk=100_000_000_000,uptime=86400*12,level=''),
       dict(id='node/pve2',type='node',node='pve2',status='online',cpu=0.05,maxcpu=8,mem=9_000_000_000,maxmem=34_359_738_368,disk=10_000_000_000,maxdisk=100_000_000_000,uptime=86400*40,level='')]
STOR=[dict(id='storage/pve1/local',type='storage',storage='local',node='pve1',plugintype='dir',content='iso,vztmpl,backup',disk=40_000_000_000,maxdisk=100_000_000_000,status='available'),
      dict(id='storage/pve1/local-lvm',type='storage',storage='local-lvm',node='pve1',plugintype='lvmthin',content='images,rootdir',disk=300_000_000_000,maxdisk=400_000_000_000,status='available'),
      dict(id='storage/pve2/nas',type='storage',storage='nas',node='pve2',plugintype='nfs',content='backup,iso',disk=1_500_000_000_000,maxdisk=4_000_000_000_000,status='available')]
TASKS=[]
def upid(node,typ,vid,user='root@pam'):
    t=int(time.time()); return f"UPID:{node}:0000A1B2:0012C3D4:{t:08X}:{typ}:{vid}:{user}:"
for i,(n,t,v,st) in enumerate([('pve1','qmstart','100','OK'),('pve2','vzdump','200','OK'),('pve1','qmshutdown','101','ERROR: VM quit/powerdown failed - got timeout'),('pve2','aptupdate','','OK')]):
    u=upid(n,t,v); TASKS.append(dict(upid=u,node=n,type=t,id=v,user='root@pam',starttime=now-600*(i+1),endtime=now-600*(i+1)+20,status=st))
PENDING={}
SNAPS={100:[dict(name='before-upgrade',description='apt full-upgrade 전',snaptime=now-86400*2,vmstate=0,parent=None),dict(name='current',description='You are here!',digest='x')]}
# ---- 확장 경로: routes/*.py 가 route(method, regex)(handler) 로 등록한다 ----
ROUTES=[]
def route(method, pattern):
    def deco(fn):
        ROUTES.append((method, re.compile(pattern), fn)); return fn
    return deco
def load_routes():
    here=os.path.dirname(os.path.abspath(__file__))
    for path in sorted(glob.glob(os.path.join(here,'routes','*.py'))):
        spec=importlib.util.spec_from_file_location('route_'+os.path.basename(path)[:-3], path)
        mod=importlib.util.module_from_spec(spec); mod.route=route; mod.STATE=sys.modules[__name__]; spec.loader.exec_module(mod)
STATE_FLAGS={}
class Status(tuple):
    """경로 처리기가 오류 상태로 답할 때: return STATE.Status(403, None)"""
    def __new__(cls, code, data=None): return tuple.__new__(cls, (code, data))
# 경로 처리기가 raise STATE.PveError(500, '...') 하면 실서버처럼 {"data":null,"message":...} 로 거절한다(digest 불일치 등)
class PveError(Exception):
    def __init__(self, code, message): super().__init__(message); self.code=code; self.message=message
class H(BaseHTTPRequestHandler):
    def ext(self, method, path, body=None):
        for m, rx, fn in ROUTES:
            if m==method:
                mm=rx.fullmatch(path)
                if mm:
                    q={k:v[0] for k,v in urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query).items()}
                    try: r=fn(mm, q, body or {})
                    except PveError as e: self.send_error_json(e.code, e.message); return True
                    if isinstance(r, Status): self.send(r[1], r[0])
                    else: self.send(r)
                    return True
        return False
    def log_message(self, fmt, *a): sys.stderr.write("REQ %.3f %s\n"%(time.time()%1000, fmt%a))
    def send(self, data, code=200):
        b=json.dumps({'data':data}).encode(); self.send_response(code); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(b))); self.end_headers(); self.wfile.write(b)
    def send_error_json(self, code, message):
        b=json.dumps({'data':None,'message':message}).encode(); self.send_response(code, message.strip()); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(b))); self.end_headers(); self.wfile.write(b)
    def expired(self, path):
        # /__fake/expire 뒤로는 티켓 갱신까지 모두 거부 — 세션 만료 처리 검사용
        if path=='/__fake/expire':
            STATE_FLAGS['expired']=True; self.send(True); return True
        # /__fake/termdrop?on=1 — 터미널 웹소켓을 인증("OK") 직후 바로 닫는다(연결하자마자 끊기는 경우 검사), on=0 으로 끈다
        if path=='/__fake/termdrop':
            STATE_FLAGS['termdrop']=urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query).get('on',['1'])[0]=='1'
            self.send(STATE_FLAGS['termdrop']); return True
        if STATE_FLAGS.get('expired'):
            sys.stderr.write("EXPIRED %s\n"%path); self.send(None,401); return True
        return False
    def do_GET(self):
        p=urllib.parse.urlparse(self.path); path=urllib.parse.unquote(p.path.replace('/api2/json',''))
        if self.expired(path): return
        # 느린 서버 흉내 — 모든 GET 에 지연(왕복), 저장소 파일 목록은 따로 더 느리게(디스크를 훑는 실서버처럼)
        delay=int(os.environ.get('FAKEPVE_DELAY_MS','0'))
        if '/content' in path: delay+=int(os.environ.get('FAKEPVE_SLOW_CONTENT_MS','0'))
        if delay: time.sleep(delay/1000)
        if path.endswith('/vncwebsocket') and self.headers.get('Upgrade','').lower()=='websocket':
            # termproxy 가 준 포트(5901)면 가짜 셸, 아니면 VNC 화면
            if urllib.parse.parse_qs(p.query).get('port',[''])[0]=='5901':
                import term; term.serve(self, drop=STATE_FLAGS.get('termdrop', False)); self.close_connection=True; return
            import vnc; vnc.serve(self); self.close_connection=True; return
        m=re.match(r'/nodes/[^/]+/qemu/(\d+)/rdp/([^/]+)$',path)
        if m and self.headers.get('Upgrade','').lower()=='websocket':
            import rdp; rdp.serve(self, int(m.group(1)), m.group(2)); self.close_connection=True; return # RDCleanPath 프록시
        if self.ext('GET', path): return
        if path=='/version': return self.send(dict(version='9.0.10',release='9.0',repoid='abcdef'))
        if path=='/cluster/resources':
            q=urllib.parse.parse_qs(p.query).get('type',[''])[0]
            if q=='vm': return self.send(GUESTS)
            if q=='storage': return self.send(STOR)
            return self.send(GUESTS+NODES+STOR)
        if path=='/cluster/tasks': return self.send(TASKS)
        m=re.match(r'/nodes/(\w+)/status$',path)
        if m: return self.send(dict(cpu=0.18,cpuinfo=dict(cores=8,sockets=2,cpus=16,model='AMD EPYC 7302P 16-Core Processor'),kversion='Linux 6.14.8-2-pve #1 SMP PREEMPT_DYNAMIC',uptime=86400*12,loadavg=['0.52','0.61','0.70'],memory=dict(used=40_000_000_000,total=68_719_476_736,free=1),swap=dict(used=100_000_000,total=8_589_934_592,free=1),rootfs=dict(used=20_000_000_000,total=100_000_000_000,avail=80_000_000_000)))
        m=re.match(r'/nodes/(\w+)/(qemu|lxc)/(\d+)/status/current',path)
        if m:
            g=[x for x in GUESTS if x['vmid']==int(m.group(3))][0]; return self.send(dict(status=g['status'],qmpstatus=g.get('qmp',g['status'])))
        m=re.match(r'/nodes/(\w+)/(qemu|lxc)/(\d+)/snapshot$',path)
        if m: return self.send(SNAPS.get(int(m.group(3)),[dict(name='current',description='You are here!')]))
        m=re.match(r'/nodes/(\w+)/(qemu|lxc)/(\d+)/config',path)
        if m: return self.send(dict(name='web-01',cores=4,sockets=1,memory=4096,ostype='l26',net0='virtio=BC:24:11:AA:BB:CC,bridge=vmbr0,firewall=1',scsi0='local-lvm:vm-100-disk-0,iothread=1,size=32G',boot='order=scsi0;net0',agent='1',digest='abc',description='웹 서버 (프로덕션)'))
        m=re.match(r'/nodes/(\w+)/qemu/(\d+)/agent/network-get-interfaces',path)
        if m: return self.send(dict(result=[{'name':'lo','ip-addresses':[{'ip-address':'127.0.0.1','ip-address-type':'ipv4','prefix':8}]},{'name':'eth0','ip-addresses':[{'ip-address':'192.168.10.21','ip-address-type':'ipv4','prefix':24},{'ip-address':'fe80::1','ip-address-type':'ipv6','prefix':64},{'ip-address':'2001:db8::21','ip-address-type':'ipv6','prefix':64}]}]))
        m=re.match(r'/nodes/(\w+)/lxc/(\d+)/interfaces',path)
        if m: return self.send([dict(name='eth0',inet='192.168.10.53/24',inet6='fe80::2/64',hwaddr='x')])
        m=re.match(r'/nodes/(\w+)/tasks/([^/]+)/status',path)
        if m:
            u=urllib.parse.unquote(m.group(2)); t=PENDING.get(u)
            if t and time.time()<t['done']: return self.send(dict(status='running',upid=u,starttime=int(t['start'])))
            if t and not t.get('applied'): t['applied']=True; t['apply']()
            return self.send(dict(status='stopped',exitstatus='OK',upid=u,starttime=now,endtime=int(time.time())))
        m=re.match(r'/nodes/(\w+)/tasks/([^/]+)/log',path)
        if m: return self.send([dict(n=1,t='starting task'),dict(n=2,t='doing things...'),dict(n=3,t='TASK OK')])
        # 모르는 목록 요청은 빈 목록으로 — 화면은 뜨고, 처리 못 한 경로는 로그에 남긴다
        sys.stderr.write("UNHANDLED GET %s\n"%path); self.send([])
    def do_POST(self):
        p=urllib.parse.urlparse(self.path); path=p.path.replace('/api2/json','')
        ln=int(self.headers.get('Content-Length',0)); body=urllib.parse.parse_qs(self.rfile.read(ln).decode())
        flat={k:v[0] for k,v in body.items()}
        if self.expired(path): return
        if path.endswith('/termproxy'):
            sys.stderr.write("TERM termproxy %s %s\n"%(path, flat))
            return self.send(dict(port=5901, ticket='PVEVNC:TERM', user='root@pam', upid=upid('pve1','vncshell','')))
        if path.endswith('/vncproxy'):
            return self.send(dict(port=5900, ticket='PVEVNC:FAKE', user='root@pam', cert='', upid=upid('pve1','vncproxy','100')))
        if path!='/access/ticket' and self.ext('POST', urllib.parse.unquote(path), flat): return
        if path=='/access/ticket':
            if STATE_FLAGS.get('ticket_delay_ms'): time.sleep(int(STATE_FLAGS['ticket_delay_ms'])/1000) # 연결 취소 검사
            # 2단계 인증 사용자 tfa@pam / secret — TOTP 123456, 복구 키 1234-5678-9abc-def0(PVE 7.2+ 새 형식)
            pw=flat.get('password','')
            # 보안 키 사용자 webauthn@pam / secret — 질문에 webauthn(+totp). 응답은 서명 검증 없이 모양만 본다:
            # webauthn:{id,type,challenge,rawId,response{authenticatorData,clientDataJSON,signature}},
            # clientDataJSON 의 type·challenge·origin("https://"+Host 헤더)이 맞아야 통과
            if flat.get('username')=='webauthn@pam':
                if 'tfa-challenge' in flat:
                    ok=False
                    if pw.startswith('webauthn:'):
                        try:
                            import base64
                            b64=lambda t: base64.urlsafe_b64decode(t+'='*(-len(t)%4))
                            r=json.loads(pw[len('webauthn:'):]); cd=json.loads(b64(r['response']['clientDataJSON']))
                            ok=(r.get('type')=='public-key' and r.get('challenge')==WEBAUTHN_CHALLENGE and r.get('id')==r.get('rawId')
                                and b64(r['id'])==b'fake-credential' and len(b64(r['response']['authenticatorData']))>=37
                                and len(b64(r['response']['signature']))>0 and cd.get('type')=='webauthn.get'
                                and cd.get('challenge')==WEBAUTHN_CHALLENGE and cd.get('origin')=='https://'+self.headers.get('Host',''))
                            sys.stderr.write("TFA webauthn clientData %s\n"%json.dumps(cd))
                        except Exception as e: sys.stderr.write("TFA webauthn bad %r\n"%e)
                    elif pw=='totp:123456': ok=True
                    sys.stderr.write("TFA answer %s %s\n"%(pw.split(':')[0], 'ok' if ok else 'rejected'))
                    if ok: return self.send(dict(ticket='PVE:webauthn@pam:0000::sig',CSRFPreventionToken='csrf',username='webauthn@pam'))
                    return self.send(None,401)
                if pw.startswith('PVE:webauthn@pam:'): return self.send(dict(ticket='PVE:webauthn@pam:0001::sig',CSRFPreventionToken='csrf',username='webauthn@pam'))
                if pw!='secret': return self.send(None,401)
                chal=json.dumps(dict(totp=True,webauthn=dict(publicKey=dict(challenge=WEBAUTHN_CHALLENGE,timeout=60000,rpId='pve.example',
                    allowCredentials=[dict(type='public-key',id='ZmFrZS1jcmVkZW50aWFs')],userVerification='discouraged'))),separators=(',',':'))
                return self.send(dict(ticket='PVE:!tfa!'+urllib.parse.quote(chal,safe="{}[],\"!'()*")+':%08X::sig'%int(time.time()),username='webauthn@pam',NeedTFA=1,CSRFPreventionToken='csrf'))
            if flat.get('username')=='tfa@pam' or 'tfa-challenge' in flat:
                if 'tfa-challenge' in flat:
                    ok=flat['tfa-challenge'].startswith('PVE:!tfa!') and pw in ('totp:123456','recovery:1234-5678-9abc-def0')
                    sys.stderr.write("TFA answer %s %s\n"%(pw.split(':')[0], 'ok' if ok else 'rejected'))
                    if ok: return self.send(dict(ticket='PVE:tfa@pam:0000::sig',CSRFPreventionToken='csrf',username='tfa@pam'))
                    return self.send(None,401)
                if pw.startswith('PVE:tfa@pam:'): return self.send(dict(ticket='PVE:tfa@pam:0001::sig',CSRFPreventionToken='csrf',username='tfa@pam'))
                if pw!='secret': return self.send(None,401)
                chal=json.dumps(dict(totp=True,recovery=[0,2,5,7]),separators=(',',':'))
                return self.send(dict(ticket='PVE:!tfa!'+urllib.parse.quote(chal,safe="{}[],\"!'()*")+':%08X::sig'%int(time.time()),username='tfa@pam',NeedTFA=1,CSRFPreventionToken='csrf'))
            if pw.startswith('PVE:!tfa!'): return self.send(None,401) # 반쪽 티켓은 비밀번호로 못 쓴다(incomplete ticket)
            if body.get('password',[''])[0] in ('secret',) or body.get('password',[''])[0].startswith('PVE:'):
                return self.send(dict(ticket='PVE:root@pam:0000::sig',CSRFPreventionToken='csrf',username='root@pam'))
            return self.send(None,401)
        m=re.match(r'/nodes/(\w+)/(qemu|lxc)/(\d+)/status/(\w+)',path)
        if m:
            vid=int(m.group(3)); act=m.group(4); g=[x for x in GUESTS if x['vmid']==vid][0]
            typ=('qm' if m.group(2)=='qemu' else 'vz')+act; u=upid(m.group(1),typ,str(vid))
            def apply():
                if act in('start','resume'): g['status']='running'; g['qmp']='running'; g['uptime']=5; g['cpu']=0.3; g['mem']=g['maxmem']//3
                elif act in('stop','shutdown'): g['status']='stopped'; g['qmp']='stopped'; g['uptime']=0; g['cpu']=0; g['mem']=0
                elif act=='suspend': g['qmp']='paused'
            td=dict(upid=u,node=m.group(1),type=typ,id=str(vid),user='root@pam',starttime=int(time.time()),status=None)
            def apply2(a=apply,td=td): a(); td['status']='OK'; td['endtime']=int(time.time())
            PENDING[u]=dict(start=time.time(),done=time.time()+1.5,apply=apply2)
            TASKS.insert(0,td)
            return self.send(u)
        sys.stderr.write("UNHANDLED POST %s %s\n"%(path, flat)); self.send(None)
    def do_PUT(self):
        p=urllib.parse.urlparse(self.path); path=urllib.parse.unquote(p.path.replace('/api2/json',''))
        ln=int(self.headers.get('Content-Length',0)); flat={k:v[0] for k,v in urllib.parse.parse_qs(self.rfile.read(ln).decode()).items()}
        if self.ext('PUT', path, flat): return
        sys.stderr.write("UNHANDLED PUT %s %s\n"%(path, flat)); self.send(None)
    def do_DELETE(self):
        p=urllib.parse.urlparse(self.path); path=urllib.parse.unquote(p.path.replace('/api2/json',''))
        if self.ext('DELETE', path): return
        sys.stderr.write("UNHANDLED DELETE %s\n"%path); self.send(None)
if __name__=='__main__':
    port=int(sys.argv[1]) if len(sys.argv)>1 else 8006
    here=os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, here)
    cert=os.path.join(os.environ.get('TMPDIR','/tmp'),'fakepve-cert'); os.makedirs(cert,exist_ok=True)
    c,k=os.path.join(cert,'cert.pem'),os.path.join(cert,'key.pem')
    if not os.path.exists(c):
        subprocess.run(['openssl','req','-x509','-newkey','rsa:2048','-nodes','-keyout',k,'-out',c,'-days','30','-subj','/CN=pve1.local/O=PVE Cluster Manager CA'],check=True,capture_output=True)
    load_routes()
    srv=ThreadingHTTPServer((os.environ.get('FAKEPVE_BIND','127.0.0.1'),port),H)
    ctx=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); ctx.load_cert_chain(c,k); srv.socket=ctx.wrap_socket(srv.socket,server_side=True)
    print('listening',port,flush=True); srv.serve_forever()

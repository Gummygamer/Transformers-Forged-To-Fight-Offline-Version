#!/usr/bin/env python3
"""One disposable LAN peer for a real phone test. The phone's test build points at this PC on UDP 17789.
The peer serves a single level-one defender and keeps all data outside the repository.
"""
import json, os, pathlib, signal, subprocess, time
HERE=pathlib.Path(__file__).resolve().parent
REPO=HERE.parents[3]
ROOT=pathlib.Path(os.environ.get('TFTF_RAID_PEER_DIR',str(pathlib.Path.home()/'tftf-build/raid-phone-peer')))
MONO=pathlib.Path(os.environ['UNITY_2020_ROOT'])/'Editor/Data/MonoBleedingEdge/bin'
ROOT.mkdir(parents=True,exist_ok=True)
state=ROOT/'server';state.mkdir(exist_ok=True)
base=state/'.fakeserver-raid-base.json'
if not base.exists():
    base.write_text(json.dumps({'placements':{'sock_boss_25_30':{'entityType':'boss','parentEntityType':'bcg','key':'kickback_gs_kabam','character':'kickback_gs_kabam','rank':1,'level':1,'sig_lvl':0,'position':{'x':25,'y':30}}}}))
sources=[HERE/'Harness.cs',HERE.parent/'net_lockstep_test/UnityStub.cs']+[REPO/'mono310/launcher/shell/Tftf'/n for n in ['TftfNet.cs','TftfBuildConfig.cs','TftfMesh.cs','TftfRaidNet.cs','Odr/JsonLite.cs']]
subprocess.run([str(MONO/'mcs'),'-out:'+str(ROOT/'raid.exe'),*map(str,sources)],check=True)
children=[];logs=[]
def start(args,name,env=None):
    f=open(ROOT/name,'a');logs.append(f)
    p=subprocess.Popen(list(map(str,args)),cwd=REPO,env=env,stdout=f,stderr=subprocess.STDOUT);children.append(p);return p
try:
    start(['legible','run','Server/run_local.lbl'],'server.log',dict(os.environ,HTTP_PORT='17788',TFTF_LOG_DIR=str(state),TFTF_TUTORIAL_BLOCKLIST=''))
    start(['python3','Server/p2p_bootstrap/bootstrap.py','--host','0.0.0.0','--port','17789'],'bootstrap.log')
    time.sleep(1)
    peer=start([MONO/'mono',ROOT/'raid.exe','node','192.168.0.139:17789','http://127.0.0.1:17788',ROOT/'peer','LanDefender','1800'],'peer.log')
    (ROOT/'peer.pid').write_text(str(peer.pid))
    print('LAN peer ready; worker PID',peer.pid,'state',ROOT,flush=True)
    signal.signal(signal.SIGTERM,lambda *_: (_ for _ in ()).throw(KeyboardInterrupt()))
    while True:time.sleep(1)
except KeyboardInterrupt:pass
finally:
    for p in children:
        if p.poll() is None:p.terminate()
    for p in children:
        try:p.wait(timeout=5)
        except subprocess.TimeoutExpired:p.kill();p.wait()
    for f in logs:f.close()

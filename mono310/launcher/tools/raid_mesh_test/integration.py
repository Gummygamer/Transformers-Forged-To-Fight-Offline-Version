#!/usr/bin/env python3
"""Two independent local servers and mesh clients: discovery, offline attack, durable result delivery and replay.
Runs only against a disposable localhost bootstrap. Never connects to production or changes a phone save.
"""
import json, os, pathlib, socket, subprocess, tempfile, time, urllib.request
HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parents[3]
MONO = pathlib.Path(os.environ['UNITY_2020_ROOT']) / 'Editor/Data/MonoBleedingEdge/bin'

def port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]

def wait_for(fn, label, seconds=80):
    end = time.time() + seconds
    while time.time() < end:
        try:
            value = fn()
            if value:
                print('ok', label, flush=True)
                return value
        except (OSError, ValueError, KeyError, urllib.error.URLError):
            pass
        time.sleep(.5)
    raise AssertionError('Timed out: ' + label)

def run():
    children, handles = [], []
    root = pathlib.Path(tempfile.mkdtemp(prefix='tftf-raid-integration-'))
    print('logs:', root, flush=True)
    def start(args, log, env=None):
        f = open(root / log, 'w'); handles.append(f)
        p = subprocess.Popen(list(map(str, args)), cwd=REPO, env=env, stdout=f, stderr=subprocess.STDOUT)
        children.append(p); return p
    def read(path):
        return json.loads(path.read_text())
    ports = [port() for _ in range(3)]
    def request(which, path, body=None):
        data = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(f'http://127.0.0.1:{ports[which]}{path}', data=data, headers={'Content-Type':'application/json'})
        with urllib.request.urlopen(req, timeout=8) as r:
            envelope = json.load(r)
        assert envelope.get('error') is None, envelope.get('error')
        return envelope['result']
    def node(which, name):
        return start([MONO/'mono', root/'raid.exe', 'node', f'127.0.0.1:{ports[2]}', f'http://127.0.0.1:{ports[which]}', root/name, name, '300'], name+'-node.log')
    try:
        sources = [HERE/'Harness.cs', HERE.parent/'net_lockstep_test/UnityStub.cs']
        sources += [REPO/'mono310/launcher/shell/Tftf'/n for n in ['TftfNet.cs','TftfBuildConfig.cs','TftfMesh.cs','TftfRaidNet.cs','Odr/JsonLite.cs']]
        subprocess.run([str(MONO/'mcs'), '-out:'+str(root/'raid.exe'), *map(str,sources)], check=True)
        for i, name in enumerate(['A','B']):
            state = root/(name+'-server'); state.mkdir()
            env = dict(os.environ, HTTP_PORT=str(ports[i]), TFTF_LOG_DIR=str(state), TFTF_TUTORIAL_BLOCKLIST='')
            start(['legible','run','Server/run_local.lbl'], name+'-server.log', env)
            wait_for(lambda i=i: request(i,'/tftf/raid/fields'), name+' bridge ready')
        start(['python3','Server/p2p_bootstrap/bootstrap.py','--host','127.0.0.1','--port',ports[2]], 'bootstrap.log')
        a, b = node(0,'A'), node(1,'B')
        def foreign(which,name):
            state = read(root/(name+'-server')/'.fakeserver-raid-network.json')
            return state if state.get('snapshots') else None
        net_a = wait_for(lambda: foreign(0,'A'), 'A learned signed B snapshot')
        net_b = wait_for(lambda: foreign(1,'B'), 'B learned signed A snapshot')
        uid_a, uid_b = net_a['uid'], net_b['uid']
        assert uid_a != uid_b and net_a['snapshots'][0]['uid'] == uid_b
        # Stop B's network, retain its server and persistent files, then raid its cached snapshot.
        b.terminate(); b.wait(timeout=5)
        found = request(0, '/matches/find-match/raid', {})
        assert len(found['matches']) == 3 and found['matches'][0]['uid'] == uid_b
        old_version = net_a['snapshots'][0]['version']
        started = request(0, '/matches/activate-match/raid', {'midx':0, 'team':['optimusprime_cin_tf']})
        assert started['activeQuests']['raid.1']['opponent'] == uid_b
        profile = request(0, '/userprofile', {'uid':uid_b})
        assert profile['name'] == 'B' and profile['gamestats']['top']['h'] and profile['gamestats']['bpi'] > 0
        request(0,'/quests/quest-join/raid.1-0',{'tm0':'optimusprime_cin_tf'})
        frozen = read(root/'A-server/.fakeserver-raid.json')['active']['snapshot']
        assert frozen['version'] == old_version
        for i in range(len(frozen['defenders'])):
            request(0,'/quests/quest-movedir/raid.1-0/1/0',{})
            request(0,'/matches/resolve-match/quests_fight',{'qid':'raid.1-0','id':str(i),'result':'WON'})
        result = read(root/'A-server/.fakeserver-raid.json')['results'][0]
        assert result['attackerWon'] and result['networkDefender'] == uid_b
        print('ok cached offline player raid and foreign profile', flush=True)
        wait_for(lambda: (root/'A/tftf_raid_outbox.txt').exists() and (root/'A/tftf_raid_outbox.txt').read_text().strip(), 'result durable while B is offline')
        a.terminate(); a.wait(timeout=5)
        a = node(0,'A')
        wait_for(lambda: (root/'A-node.log').exists() and (root/'A/tftf_raid_outbox.txt').read_text().strip(), 'attacker restart retains queued result')
        # B changes its base before reconnecting. The result must retain the attacked version.
        base = request(1,'/base/active')['userBase']['placements']
        socket_id = next(k for k,v in base.items() if v['entityType']=='boss')
        x,y = base[socket_id]['position'].values()
        changed = request(1,f'/base/place/user/boss/optimusprime_cin_tf/user_base_1/{x}/{y}/{socket_id}',{})
        assert changed['success']
        b = node(1,'B')
        def delivered():
            path=root/'B-server/.fakeserver-raid.json'
            if not path.exists(): return None
            return next((r for r in read(path)['results'] if r['attacker']==uid_a and r['defender']=='1000000000001'),None)
        defence = wait_for(delivered,'B receives offline defence report',110)
        assert defence['attackerWon']
        health=defence['journal'][2]['updates']['defenders']
        assert len(health)==len(frozen['defenders']) and all(d['hp']==0 for d in health)
        assert len(defence['journal'][0]['defenseTeam']['entities']) == len(frozen['defenders'])
        wait_for(lambda: not (root/'A/tftf_raid_outbox.txt').read_text().strip(), 'signed acknowledgment clears A outbox')
        # Replay the stored incoming event through the bridge: no duplicate report or shield extension.
        state = read(root/'B-server/.fakeserver-raid-network.json'); shield=state['shield']
        before=len(read(root/'B-server/.fakeserver-raid.json')['results'])
        incoming={'id':defence['_id'],'attacker':uid_a,'defender':uid_b,'won':True,'team':['optimusprime_cin_tf'],'snapshot':frozen}
        request(1,'/tftf/raid/sync',{'uid':uid_b,'snapshots':[],'incoming':incoming})
        assert len(read(root/'B-server/.fakeserver-raid.json')['results'])==before
        assert read(root/'B-server/.fakeserver-raid-network.json')['shield']==shield
        print('ok replay has no duplicate report or extended shield',flush=True)
        print('raid mesh integration passed',flush=True)
    finally:
        for p in children:
            if p.poll() is None: p.terminate()
        for p in children:
            try:p.wait(timeout=6)
            except subprocess.TimeoutExpired:p.kill();p.wait()
        for f in handles:f.close()

if __name__=='__main__':run()

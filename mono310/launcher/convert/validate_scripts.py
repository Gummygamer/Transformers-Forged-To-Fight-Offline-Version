"""Checks that the script layouts built by the Editor (TypeTreeDump) parse every MonoBehaviour of the original 5.3.5
player data byte-exactly. Usage: validate_scripts.py <apk Data dir> <typetrees bundle> [class names...]"""
import sys, UnityPy, os, collections, pickle
from UnityPy.helpers import Tpk
import sys; sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from tt_db import clone, script_tree
d=sys.argv[1].rstrip('/')+'/'; DUMP=sys.argv[2]
mb=Tpk.get_typetree_node(114,(5,3,5,8))
base=list(mb.m_Children)           # 5.3.5 base children
prefix=clone(mb, base[:3])
# DB from Editor dump (2020 trees), base swapped to 5.3.5
P=DUMP
env=UnityPy.load(P); db={}
for o in env.objects:
    if o.type.name!='MonoBehaviour': continue
    m=o.read(); sc=m.m_Script.deref().read(); key=(sc.m_AssemblyName,sc.m_Namespace,sc.m_ClassName)
    n=o.assets_file.types[o.type_id].node
    db[key]=script_tree(n)
print('db',len(db),[ (c.m_Name,c.m_Type) for c in mb.m_Children])
files=[f for f in os.listdir(d) if os.path.isfile(d+f) and f not in ('settings.xml','splash.png') and '.split' not in f and f!='unity default resources']
envs={}
def load(name):
    if name not in envs: envs[name]=list(UnityPy.load(d+name).files.values())[0]
    return envs[name]
sc_cache={}
def script_key(sf,fid,pid):
    owner=sf if fid==0 else load(sf.externals[fid-1].path.split('/')[-1])
    k=(id(owner),pid)
    if k not in sc_cache:
        t=owner.objects[pid].read_typetree(); sc_cache[k]=(t['m_AssemblyName'],t['m_Namespace'],t['m_ClassName'])
    return sc_cache[k]
ONLY=set(sys.argv[3:])
ok=collections.Counter(); bad=collections.Counter(); ex={}
for f in files:
    try: sf=load(f)
    except Exception: continue
    if not hasattr(sf,'objects'): continue
    for o in sf.objects.values():
        if o.type.name!='MonoBehaviour': continue
        try:
            p=o.read_typetree(prefix,check_read=False)['m_Script']; key=script_key(sf,p['m_FileID'],p['m_PathID'])
        except Exception: continue
        if key not in db or (ONLY and key[2] not in ONLY): continue
        try:
            o.read_typetree(db[key],check_read=True); ok[key]+=1
        except Exception as e:
            bad[key]+=1; ex.setdefault(key,(f,o.path_id,repr(e)[:90]))
    if len(envs)>60:
        for k in list(envs):
            if k!='globalgamemanagers.assets': envs.pop(k)
print('ok objects',sum(ok.values()),'scripts',len(ok)); print('bad objects',sum(bad.values()),'scripts',len(bad))
for k,n in bad.most_common(40): print(' ',k,n,ex[k])

import UnityPy, os, collections, sys
from UnityPy.helpers import Tpk
import sys; d=sys.argv[1].rstrip('/')+'/'
ok=collections.Counter(); bad=collections.Counter(); err=collections.Counter(); ex={}
files=[f for f in os.listdir(d) if os.path.isfile(d+f) and f not in ('settings.xml','splash.png') and '.split' not in f and f!='unity default resources']
for f in files:
    try: env=UnityPy.load(d+f)
    except Exception as e: continue
    sf=list(env.files.values())[0]
    if not hasattr(sf,'objects'): continue
    ver=sf.version if hasattr(sf,'version') else None
    for o in sf.objects.values():
        n=o.type.name
        if n in ('MonoBehaviour','Shader','CGProgram','MonoScript'): continue
        try:
            node=Tpk.get_typetree_node(o.class_id,(5,3,5,8))
            o.read_typetree(node, check_read=True)
            ok[n]+=1
        except Exception as e:
            err[n]+=1; ex.setdefault(n,(f,o.path_id,repr(e)[:100]))
print('ok',dict(ok)); print('bad',dict(bad)); print('err',dict(err)); print(ex)

"""Type-tree database for converting Unity 5.3.5 player data (no type trees) into bundles a 2020 player can load.

Engine classes: trees from UnityPy's bundled class database (MIT, Unity layouts for 5.3.5).
Scripts: layouts built by the Unity 2020 Editor from the recompiled scripts (TypeTreeDump.cs), with the 5.3.5
base fields and the 5.3.5 layout of the built-in structs whose binary layout changed since.
"""
from UnityPy.helpers import Tpk
from UnityPy.helpers.TypeTreeNode import TypeTreeNode

VER = (5, 3, 5, 8)


def clone(n, children=None, level=None):
    return TypeTreeNode(m_Level=n.m_Level if level is None else level, m_Type=n.m_Type, m_Name=n.m_Name,
                        m_ByteSize=n.m_ByteSize, m_Version=n.m_Version, m_TypeFlags=n.m_TypeFlags,
                        m_MetaFlag=n.m_MetaFlag, m_Index=n.m_Index, m_VariableCount=n.m_VariableCount,
                        m_RefTypeHash=n.m_RefTypeHash,
                        m_Children=list(n.m_Children if children is None else children))


def relevel(n, level):
    return clone(n, [relevel(c, level + 1) for c in n.m_Children], level)


def engine(class_id):
    return Tpk.get_typetree_node(class_id, VER)


# Built-in structs whose binary layout changed after Unity 5.3.5. Verified byte-for-byte on the original data:
#   Keyframe: 4 floats (the 2020 weightedMode/inWeight/outWeight fields do not exist),
#   Gradient: 8 packed 32-bit colour keys, 8+8 uint16 times, two uint8 counts, no m_Mode.
DROP_FIELDS = {"Keyframe": ("weightedMode", "inWeight", "outWeight")}
PARTICLE_SYSTEM = 198
_packed_color = None


def _find_packed_color():
    global _packed_color
    if _packed_color is None:
        stack = list(engine(PARTICLE_SYSTEM).m_Children)
        while stack:
            x = stack.pop()
            if x.m_Type == "ColorRGBA" and len(x.m_Children) == 1 and x.m_Children[0].m_Type == "unsigned int":
                _packed_color = x
                break
            stack.extend(x.m_Children)
    return _packed_color


def _gradient_535(n):
    color = _find_packed_color()
    lvl = n.m_Level + 1
    kids = []
    for i in range(8):
        k = relevel(color, lvl)
        k.m_Name = "key%d" % i
        kids.append(k)
    keep = [clone(c) for c in n.m_Children
            if c.m_Name.startswith(("ctime", "atime")) or c.m_Name in ("m_NumColorKeys", "m_NumAlphaKeys")]
    out = clone(n, kids + keep)
    out.m_ByteSize = sum(c.m_ByteSize for c in out.m_Children)
    return out


def swap_structs(n):
    if n.m_Type == "Gradient":
        return _gradient_535(n)
    drop = DROP_FIELDS.get(n.m_Type, ())
    kids = [swap_structs(c) for c in n.m_Children if c.m_Name not in drop]
    out = clone(n, kids)
    if drop and out.m_ByteSize > 0:
        out.m_ByteSize = sum(c.m_ByteSize for c in kids if c.m_ByteSize > 0)
    return out


def script_tree(dumped_node):
    """5.3.5 tree for a script from its 2020 Editor-built tree."""
    base = list(engine(114).m_Children)
    tail = list(dumped_node.m_Children)[4:]       # fields after m_GameObject/m_Enabled/m_Script/m_Name
    return swap_structs(clone(dumped_node, base + tail))

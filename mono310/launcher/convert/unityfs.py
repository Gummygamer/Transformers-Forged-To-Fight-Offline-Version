"""Minimal UnityFS (bundle format 6) writer: uncompressed 128 KB blocks (like genuine bundles), any number of nodes."""
import struct


def write_unityfs(nodes, engine="5.3.5p8", player="5.x.x"):
    """nodes: list of (name, bytes, flags). flags: 4 = serialized file, 0 = raw (.resource)."""
    data = b"".join(n[1] for n in nodes)
    info = bytearray(b"\0" * 16)                           # uncompressed data hash
    BLOCK = 0x20000
    blocks = [min(BLOCK, len(data) - o) for o in range(0, len(data), BLOCK)] or [0]
    info += struct.pack(">I", len(blocks))
    for n in blocks:
        info += struct.pack(">IIH", n, n, 0)               # stored (flags 0)
    info += struct.pack(">I", len(nodes))
    off = 0
    for name, blob, flags in nodes:
        info += struct.pack(">qqI", off, len(blob), flags) + name.encode() + b"\0"
        off += len(blob)
    head = b"UnityFS\0" + struct.pack(">I", 6) + player.encode() + b"\0" + engine.encode() + b"\0"
    fixed = struct.pack(">qIII", 0, len(info), len(info), 0x40)   # flag 0x40: directory info present, no compression
    total = len(head) + len(fixed) + len(info) + len(data)
    return head + struct.pack(">qIII", total, len(info), len(info), 0x40) + bytes(info) + data

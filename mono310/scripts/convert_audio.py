#!/usr/bin/env python3
"""AssetRipper exports Unity-5 AudioClips as native .audioclip + FSB5 .resS, which crash the
batch editor on unload. All 3.1.0 clips are single-subsound FSB5/MPEG: strip the FSB5 header and
write a plain .mp3 with the same GUID (fileID 8300000 is the AudioClip main object either way).
Usage: convert_audio.py <Assets dir>"""
import re, struct, sys
from pathlib import Path

META = """fileFormatVersion: 2
guid: {guid}
AudioImporter:
  externalObjects: {{}}
  serializedVersion: 6
  defaultSettings:
    loadType: {load}
    sampleRateSetting: 0
    sampleRateOverride: 44100
    compressionFormat: 1
    quality: 1
    conversionMode: 0
  platformSettingOverrides: {{}}
  forceToMono: 0
  normalize: 1
  preloadAudioData: 1
  loadInBackground: 0
  ambisonic: 0
  3D: 1
  userData:
  assetBundleName:
  assetBundleVariant:
"""

BR1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0]
BR2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0]
SR = {3: [44100, 48000, 32000], 2: [22050, 24000, 16000], 0: [11025, 12000, 8000]}

def deframe(d):
    """FMOD pads every MPEG frame to a 4-byte boundary; drop the padding."""
    out = bytearray(); p = 0
    while p + 4 <= len(d) and d[p] == 0xFF and (d[p + 1] & 0xE0) == 0xE0:
        v = (d[p + 1] >> 3) & 3; br = (d[p + 2] >> 4) & 15; sr = (d[p + 2] >> 2) & 3; pad = (d[p + 2] >> 1) & 1
        if v == 3: size = 144 * BR1[br] * 1000 // SR[3][sr] + pad
        else: size = 72 * BR2[br] * 1000 // SR[v][sr] + pad
        if size < 4: break
        out += d[p:p + size]
        p = (p + size + 3) & ~3
    return bytes(out)

def main(assets):
    n = bad = 0
    for ac in Path(assets).rglob("*.audioclip"):
        res = Path(str(ac) + ".resS")
        meta = Path(str(ac) + ".meta")
        if not res.exists() or not meta.exists():
            bad += 1; continue
        b = res.read_bytes()
        if b[:4] != b"FSB5":
            bad += 1; continue
        _, nsamp, shs, nts, ds, mode = struct.unpack("<6I", b[4:28])
        off = 60 + shs + nts
        mp3 = deframe(b[off:off + ds])
        if nsamp != 1 or mode != 11 or not (mp3[:1] == b"\xff"):
            bad += 1; continue
        guid = re.search(r"guid: (\w+)", meta.read_text())[1]
        y = ac.read_text()
        load = int(re.search(r"m_LoadType: (\d)", y)[1])
        out = ac.with_suffix(".mp3")
        out.write_bytes(mp3)
        Path(str(out) + ".meta").write_text(META.format(guid=guid, load=load))
        for p in (ac, res, meta, Path(str(res) + ".meta")):
            if p.exists(): p.unlink()
        n += 1
    print(f"converted {n} clips, skipped {bad}")

main(sys.argv[1])

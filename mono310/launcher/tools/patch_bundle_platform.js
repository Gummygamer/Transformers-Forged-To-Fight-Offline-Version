// One-off helper: read/patch the SerializedFile target-platform stamp inside a stored (uncompressed)
// UnityFS bundle (format 6) produced by this launcher's converter (see UnityFsWriter.cs). It parses the
// directory structurally and only touches the Platform int32 in each serialized-file node header, so
// coincidental "5.3.5p8" byte runs inside asset data are never modified.
// Android = 13; StandaloneWindows = 5; StandaloneWindows64 = 19.
// Usage: node patch_bundle_platform.js <bundle> [--write <value>]
const fs = require('fs');
const file = process.argv[2];
const wi = process.argv.indexOf('--write');
const target = wi !== -1 ? parseInt(process.argv[wi + 1], 10) : null;
const b = fs.readFileSync(file);

let p = 0;
function cstr() { const s = p; while (b[p] !== 0) p++; const t = b.toString('binary', s, p); p++; return t; }
function u32be() { const v = b.readUInt32BE(p); p += 4; return v; }
function u16be() { const v = b.readUInt16BE(p); p += 2; return v; }
function i64be() { const v = Number(b.readBigInt64BE(p)); p += 8; return v; }

const magic = cstr();
if (magic !== 'UnityFS') throw new Error('not a UnityFS bundle: ' + magic);
const fmt = u32be();
const genA = cstr(), genB = cstr();
const size = i64be();
const ciBlk = u32be(), uiBlk = u32be(), flags = u32be();
console.log(`UnityFS fmt=${fmt} gen="${genA}"/"${genB}" size=${size} flags=0x${flags.toString(16)}`);
if ((flags & 0x3f) !== 0) throw new Error('compressed directory info (flags=0x' + flags.toString(16) + '); this helper only handles stored bundles');

const infoStart = p;                 // directory info (uncompressed, right after header)
p += 16;                             // data hash
const blocks = u32be();
for (let i = 0; i < blocks; i++) { u32be(); u32be(); u16be(); }  // per-block comp/uncomp/flags
const nodeCount = u32be();
const nodes = [];
for (let i = 0; i < nodeCount; i++) {
  const offset = i64be(), nsize = i64be(), nflags = u32be(), name = cstr();
  nodes.push({ offset, nsize, nflags, name });
}
const dataStart = infoStart + uiBlk; // node data begins after the directory info block

let patched = 0, serial = 0;
const versions = {};
for (const n of nodes) {
  const isSerialized = (n.nflags & 4) !== 0;
  if (!isSerialized) { continue; }   // raw resource node (textures/audio blobs)
  const nodeAbs = dataStart + n.offset;
  // serialized header: metaSize(4BE) fileSize(4BE) version(4BE) dataOffset(4BE) bigEndian(1) pad(3) CStr(unityVersion) I32(platform)
  let vp = nodeAbs + 20, ve = vp;
  while (b[ve] !== 0 && ve < vp + 32) ve++;
  const uver = b.toString('binary', vp, ve);
  if (!/^\d+\.\d+/.test(uver)) { console.log(`  node "${n.name}": header version "${uver}" not recognized; SKIPPED`); continue; }
  const platPos = ve + 1;
  const val = b.readInt32LE(platPos);
  serial++;
  versions[uver] = (versions[uver] || 0) + 1;
  if (target !== null && val !== target) { b.writeInt32LE(target, platPos); patched++; }
}
console.log(`serialized nodes: ${serial}; header unity-versions seen:`, versions);
if (target !== null) { fs.writeFileSync(file, b); console.log(`patched ${patched} serialized node(s) to platform=${target}`); }
else console.log('dry run (pass --write <value> to patch)');

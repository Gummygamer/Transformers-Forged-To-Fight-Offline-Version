// Hook EB.Dot.* accessors to dump every JSON key the game reads + whether it was found.
// Reveals the data structures each Sparx response must contain.
const RVA = {
  'Bool':    0x1455C68,
  'String':  0x144F534,
  'Find':    0x1460038,
  'SparxId': 0x145FFB8,
  'Array':   0x1460ABC,
};
function readStr(p) {
  try {
    if (p.isNull()) return '(null)';
    const len = p.add(0x10).readS32();
    if (len < 0 || len > 512) return '(?'+len+')';
    return p.add(0x14).readUtf16String(len);
  } catch (e) { return '(err)'; }
}
function waitForBase() {
  const b = Module.findBaseAddress('libil2cpp.so');
  if (b) { install(b); }
  else { setTimeout(waitForBase, 200); }
}
function install(base) {
  console.log('[*] libil2cpp.so @ ' + base);
  for (const name in RVA) {
    try {
      Interceptor.attach(base.add(RVA[name]), {
        onEnter(args) { this.key = readStr(args[0]); },
        onLeave(ret) {
          const r = ret.isNull() ? 'NULL' : 'ok';
          console.log('DOT ' + name + ' "' + this.key + '" -> ' + r);
        }
      });
    } catch (e) { console.log('hook fail ' + name + ': ' + e); }
  }
  console.log('[*] hooks installed');
}
waitForBase();

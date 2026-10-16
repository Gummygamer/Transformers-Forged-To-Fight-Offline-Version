#!/usr/bin/env python3
"""Repair ILSpy closure-capture splits (dead lambda-only locals) in decompiled C#.

Defect: inside a method, `T x;` (or `= default(T)`/null/0/false) is declared, assigned
only inside delegate/lambda bodies and never read, while an earlier local/parameter of
the SAME type is read after the lambda.  The fix renames the dead variable's lambda
assignments to the live original and drops the dead declaration.

repair_text(text) -> (new_text, count)   conservative: single unambiguous candidate only
CLI: closure_split.py --dry-run PATH...  (file or directory; prints every match)
"""
import re
import sys
import os

DECL = re.compile(r'^(\t+)([A-Za-z_][\w<>\[\],.?]*(?: ?<[^;=]*>)?) (\w+)(?: = (?:default\([^;]*\)|null|false|true|0|""))?;\s*$')
PARAM_SPLIT = re.compile(r'\(([^()]*)\)\s*$')
KEYWORDS = {"return", "yield", "goto", "throw", "else", "var", "new", "case", "break", "continue"}


def _indent(l):
    return len(l) - len(l.lstrip('\t'))


def _lambda_ranges(lines):
    """list of (start, end) line index ranges of delegate/lambda bodies (by brace indent)."""
    out = []
    n = len(lines)
    for i, l in enumerate(lines):
        if 'delegate' in l or '=>' in l:
            if re.search(r'\bdelegate\b\s*(\(|$|\{)', l) or '=>' in l:
                if l.rstrip().endswith('{'):
                    j = i
                elif i + 1 < n and lines[i + 1].strip() == '{':
                    j = i + 1
                else:
                    continue
                k = _indent(lines[j])
                for e in range(j + 1, n):
                    if lines[e].strip() and _indent(lines[e]) == k and lines[e].lstrip().startswith('}'):
                        out.append((j, e))
                        break
    return out


def _member_indent(lines):
    ms = [_indent(l) for l in lines if re.match(r'\t*(public|private|protected|internal) ', l) and '(' in l]
    return min(ms) if ms else 2


def _method_range(lines, i, mi=2):
    s = i
    while s > 0 and not (_indent(lines[s]) == mi and lines[s].strip() == '{'):
        s -= 1
    e = i
    while e < len(lines) and not (_indent(lines[e]) == mi and lines[e].strip() in ('}', '};')):
        e += 1
    # include signature line(s) above s for parameters
    sig = lines[s - 1] if s > 0 else ''
    return s, e, sig


def _occ(line, name):
    return [m for m in re.finditer(r'(?<![\w.])' + re.escape(name) + r'\b', line)]


def _is_write(line, m):
    rest = line[m.end():]
    return re.match(r'\s*=(?!=)', rest) is not None


def find_sites(text):
    lines = text.split('\n')
    lam = _lambda_ranges(lines)
    mi = _member_indent(lines)

    def in_lam(i):
        return any(a <= i <= b for a, b in lam)

    sites = []
    for i, l in enumerate(lines):
        m = DECL.match(l)
        if not m:
            continue
        ind, typ, name = len(m.group(1)), m.group(2), m.group(3)
        if typ in KEYWORDS or typ == 'var':
            continue
        ms, me, sig = _method_range(lines, i, mi)
        if not (ms < i < me):
            continue
        # scope end of declaring block
        be = i
        while be < me and not (lines[be].strip().startswith('}') and _indent(lines[be]) < ind):
            be += 1
        uses = []
        for j in range(i + 1, be):
            for mm in _occ(lines[j], name):
                uses.append((j, _is_write(lines[j], mm)))
        if not uses:
            continue
        if any(not w for _, w in uses):
            continue
        if not all(in_lam(j) for j, _ in uses):
            continue
        # candidates: same type, declared earlier in method (or param), read after the first lambda use
        first = uses[0][0]
        cands = []
        pm = PARAM_SPLIT.search(sig.strip())
        decls = []
        if pm and pm.group(1).strip():
            for p in pm.group(1).split(','):
                p = p.strip().split(' = ')[0].strip()
                parts = p.split()
                if len(parts) >= 2:
                    decls.append((' '.join(parts[:-1]).replace('params ', ''), parts[-1], ms))
        for j in range(ms, i):
            d = DECL.match(lines[j])
            if d:
                decls.append((d.group(2), d.group(3), j))
            else:  # declared with initializer
                d2 = re.match(r'^\t+([A-Za-z_][\w<>\[\],.?]*) (\w+) = ', lines[j])
                if d2 and d2.group(1) not in KEYWORDS and d2.group(1) != 'var':
                    decls.append((d2.group(1), d2.group(2), j))
        for t, n2, dj in decls:
            if t != typ or n2 == name:
                continue
            reads = [j for j in range(first, me) for mm in _occ(lines[j], n2)
                     if not _is_write(lines[j], mm)]
            if reads:
                cands.append(n2)
        sites.append({"line": i + 1, "type": typ, "dead": name, "cands": sorted(set(cands)),
                      "uses": [j + 1 for j, _ in uses]})
    return sites


def repair_text(text):
    count = 0
    # iterate: each repair shifts lines, re-scan after each fix
    while True:
        fixed = False
        for s in find_sites(text):
            if len(s["cands"]) != 1:
                continue
            lines = text.split('\n')
            dead, live = s["dead"], s["cands"][0]
            for ln in s["uses"]:
                lines[ln - 1] = re.sub(r'(?<![\w.])' + re.escape(dead) + r'\b', live, lines[ln - 1])
            del lines[s["line"] - 1]
            text = '\n'.join(lines)
            count += 1
            fixed = True
            break
        if not fixed:
            return text, count


def _iter_files(paths):
    for p in paths:
        if os.path.isdir(p):
            for r, _, fs in os.walk(p):
                for f in fs:
                    if f.endswith('.cs'):
                        yield os.path.join(r, f)
        else:
            yield p


def main(argv):
    dry = '--dry-run' in argv
    paths = [a for a in argv if not a.startswith('--')]
    if not paths:
        print(__doc__)
        return 1
    total = auto = 0
    for f in _iter_files(paths):
        try:
            t = open(f, encoding='utf-8-sig', errors='replace').read()
        except OSError:
            continue
        for s in find_sites(t):
            total += 1
            ok = len(s["cands"]) == 1
            auto += ok
            print("%s:%d  %s %s  -> %s  [%s]" % (f, s["line"], s["type"], s["dead"],
                  ",".join(s["cands"]) or "?", "AUTO" if ok else "manual"))
        if not dry:
            n, c = repair_text(t)
            if c:
                open(f, 'w', encoding='utf-8').write(n)
    print("sites=%d auto=%d" % (total, auto))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))

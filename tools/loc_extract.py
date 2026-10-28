#!/usr/bin/env python3
"""Collects the English texts of the mod: Loc.T/Loc.F literals, the settings window's rows (tab, group, label, unit, options),
the .cfg descriptions (help texts). Writes tools/loc/en.tsv: key<TAB>english (\\n escaped)."""
import re, sys, glob, os
LIT = r'@?"(?:[^"\\]|\\.)*"'
def unq(l):
    l = l[1:-1]
    out = []; i = 0
    while i < len(l):
        c = l[i]
        if c == '\\' and i + 1 < len(l):
            n = l[i+1]; i += 2
            if n == 'u' and i + 4 <= len(l): out.append(chr(int(l[i:i+4], 16))); i += 4; continue
            out.append({'n': '\n', 't': '\t', '"': '"', '\\': '\\', "'": "'"}.get(n, n))
        else: out.append(c); i += 1
    return ''.join(out)
def concat(src, pos):
    """literal ("a" + "b" ...) starting at pos; returns (text, end) or None"""
    m = re.compile(r'\s*(' + LIT + ')').match(src, pos)
    if not m: return None
    t = unq(m.group(1)); end = m.end()
    while True:
        m = re.compile(r'\s*\+\s*(' + LIT + ')').match(src, end)
        if not m: break
        t += unq(m.group(1)); end = m.end()
    return t, end
def key(en):
    k = ''; sep = True
    for c in en:
        if len(k) >= 40: break
        if c.isascii() and c.isalnum(): k += c.lower(); sep = False
        elif not sep: k += '_'; sep = True
    h = 2166136261
    for b in en.encode('utf-8'): h ^= b; h = (h * 16777619) & 0xffffffff
    return k.rstrip('_') + '.' + ('%08x' % h)[2:]
def split_args(s, pos):
    """top-level arguments of a call whose '(' is just before pos"""
    out = []; depth = 0; cur = pos; i = pos
    while i < len(s):
        c = s[i]
        if c == '"':
            i += 1
            while i < len(s) and s[i] != '"':
                i += 2 if s[i] == '\\' else 1
        elif c in '([{': depth += 1
        elif c in ')]}':
            if depth == 0: out.append(s[cur:i]); return out
            depth -= 1
        elif c == ',' and depth == 0: out.append(s[cur:i]); cur = i + 1
        i += 1
    return None
EXTRA = ['Add type', 'Game language', 'plugging (stops the leak)', 'fire', 'fighting', 'putting out', 'leak']   # texts built at run time (appended at the end: the translation files are numbered)

def scan(root):
    found = {}
    def add(t, where):
        if t.strip() and not re.fullmatch(r'[a-z]{2}', t) and not t.startswith('#') and not re.fullmatch(r'\w+(/\w+)+', t) and any(ch.isalpha() for ch in t): found.setdefault(t, where)
    for f in sorted(glob.glob(os.path.join(root, 'src', '*.cs'))):
        if f.endswith('Loc.cs'): continue
        s = open(f, encoding='utf-8-sig').read(); n = os.path.basename(f)
        for m in re.finditer(r'Loc\.[TF]\(', s):
            r = concat(s, m.end())
            if r: add(r[0], n)
            a = split_args(s, m.end())
            if a and ' ? ' in a[0]:   # ternary: every literal in the branches
                for l in re.findall(LIT, a[0]): add(unq(l), n + ' (ternary)')
        for m in re.finditer(r'static readonly string\[\]\s+(?:PriorityNames|PriorityTips|OrderText)\s*=\s*\{([^}]*)\}', s):
            for l in re.findall(LIT, m.group(1)): add(unq(l), n + ' (array)')
        for m in re.finditer(r'\b\w*[Cc]fg\.Bind\(', s):
            args = split_args(s, m.end())
            if args and len(args) >= 4:
                r = concat(args[3], 0)
                if r and r[1] == len(args[3].rstrip()): add(r[0], n + ' (cfg)')
        if n == 'SettingsWindow.cs':
            for m in re.finditer(r'\bAdd\(\s*(\w+|' + LIT + r')\s*,\s*(' + LIT + r'|\w+)\s*,\s*(?:' + LIT + r'|[\w.]+)\s*,\s*(' + LIT + r')\s*,\s*(' + LIT + r')', s):
                add(unq(m.group(3)), n + ' (label)'); add(unq(m.group(4)), n + ' (unit)')
                if m.group(2).startswith('"'): add(unq(m.group(2)), n + ' (group)')
            for m in re.finditer(r'const string\s+([^;]*);', s):
                if 'TUi' in m.group(1) or 'ArrowsGroup' in m.group(1) or 'TFx' in m.group(1):
                    for l in re.findall(LIT, m.group(1)): add(unq(l), n + ' (tab)')
            for m in re.finditer(r'(?:Subs|names)\s*=\s*\{([^}]*)\}', s):
                for l in re.findall(LIT, m.group(1)): add(unq(l), n + ' (ui)')
            for m in re.finditer(r'\bd = (' + LIT + ')', s): add(unq(m.group(1)), n + ' (help)')
            for m in re.finditer(r'(?:Ask|MapRows)\(\s*(' + LIT + ')', s): add(unq(m.group(1)), n + ' (ui)')
            for m in re.finditer(r'MapRows\(([^;]*?);', s, re.S):
                for l in re.findall(LIT, m.group(1)): add(unq(l), n + ' (ui)')
            for m in re.finditer(r'static readonly string\s+T\w+\s*=\s*(' + LIT + ')', s): add(unq(m.group(1)), n + ' (tab)')
            for m in re.finditer(r'\bTabs\s*=\s*\{([^}]*)\}', s):
                for l in re.findall(LIT, m.group(1)): add(unq(l), n + ' (tab)')
            for m in re.finditer(r'Options\s*=\s*new\[?\]?\s*(?:string\[\])?\s*\{([^}]*)\}', s):
                for l in re.findall(LIT, m.group(1)): add(unq(l), n + ' (option)')
            for m in re.finditer(r'(?:Btn|GroupTitle|Text|Hover)\(\s*(?:\w+\s*,\s*)?(' + LIT + ')', s): add(unq(m.group(1)), n + ' (ui)')
    return found
if __name__ == '__main__':
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    d = scan(root)
    out = os.path.join(root, 'tools', 'loc', 'en.tsv')
    old = []
    if os.path.exists(out):   # keep the numbering of the translation files: old rows first, new ones at the end
        for l in open(out, encoding='utf-8'):
            p = l.rstrip('\n').split('\t')
            if len(p) >= 2: old.append(p[1].replace('\\t', '\t').replace('\\n', '\n').replace('\\\\', '\\'))
    for t in old: d.setdefault(t, 'old')
    order = old + [x for x in sorted(d, key=lambda x: (d[x], x)) + EXTRA if x not in old]
    seen = set(); order = [x for x in order if not (x in seen or seen.add(x))]
    with open(out, 'w', encoding='utf-8', newline='\n') as fh:
        for t in order:
            d.setdefault(t, 'extra')
            fh.write(key(t) + '\t' + t.replace('\\', '\\\\').replace('\n', '\\n').replace('\t', '\\t') + '\t' + d[t] + '\n')
    print(len(d), 'texts ->', out)

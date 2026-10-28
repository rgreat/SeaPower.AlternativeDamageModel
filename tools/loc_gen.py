#!/usr/bin/env python3
"""Makes Release/lang/<code>.ini from tools/loc/en.tsv and tools/loc/tr/<code>.txt (lines "N|translation", N = the line of en.tsv).
Checks that {0}, {1} ... are kept. en.ini holds every English text with its key (to copy and translate)."""
import os, re, sys
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
en = []
for line in open(os.path.join(root, 'tools/loc/en.tsv'), encoding='utf-8'):
    k, t, w = line.rstrip('\n').split('\t')
    en.append((k, t, w))
out = os.path.join(root, 'Release', 'lang'); os.makedirs(out, exist_ok=True)
def esc(t): return t
def header(code, note):
    return ('; Alternative Damage Model - texts, language: %s\n; key = text   (\\n = line break; keep {0} {1} ...)\n%s\n\n' % (code, note))
def write(code, rows, note):
    with open(os.path.join(out, code + '.ini'), 'w', encoding='utf-8-sig', newline='\n') as f:
        f.write(header(code, note))
        for k, t in rows: f.write(k + ' = ' + t + '\n')
write('en', [(k, t) for k, t, w in en], '; English: the texts of the code, nothing to translate here. Copy this file to start a new language.')
langs = sys.argv[1:] or [f[:-4] for f in sorted(os.listdir(os.path.join(root, 'tools/loc/tr'))) if f.endswith('.txt')]
for code in langs:
    tr = {}
    for line in open(os.path.join(root, 'tools/loc/tr', code + '.txt'), encoding='utf-8'):
        line = line.rstrip('\n')
        if not line or line.startswith(';'): continue
        n, t = line.split('|', 1); tr[int(n)] = t
    rows = []; bad = 0
    for i, (k, t, w) in enumerate(en, 1):
        x = tr.get(i)
        if x is None: bad += 1; print(code, 'missing', i, t[:40]); continue
        e = x.replace('\\n', '\n')
        if sorted(re.findall(r'\{\d\}', t)) != sorted(re.findall(r'\{\d\}', x)): print(code, 'placeholders', i, x[:60]); continue
        rows.append((k, x.replace('\n', '\\n')))
    note = '; Machine translation, not checked by a native speaker: corrections are welcome.'
    write(code, rows, note)
    print(code, len(rows), 'of', len(en))
